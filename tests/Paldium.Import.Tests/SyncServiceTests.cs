using System.Diagnostics;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Import.Saves;
using Paldium.Import.Sync;

namespace Paldium.Import.Tests;

public class SyncServiceTests : IDisposable
{
    private sealed class FakeReader : ISaveWorldReader
    {
        private int _concurrent;
        private int _maxConcurrent;
        public int WorldReads;
        public int GlobalReads;
        public readonly List<string> WorldPaths = [];
        public Func<int, IReadOnlyList<OwnedPal>> Pals = _ => [];
        public IReadOnlyList<PlayerCapture> Captures = [];
        public Func<int, Exception?> Failure = _ => null;
        public TimeSpan Delay = TimeSpan.Zero;

        public int MaxConcurrent => _maxConcurrent;

        public IReadOnlyList<DiscoveredWorld> DiscoverLocalWorlds() => [];

        public WorldSnapshot ReadWorld(string worldFolderPath)
        {
            var attempt = Interlocked.Increment(ref WorldReads);
            lock (WorldPaths) WorldPaths.Add(worldFolderPath);
            Enter();
            try
            {
                if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
                if (Failure(attempt) is { } failure) throw failure;
                return new WorldSnapshot("w", null, [], [], Pals(attempt), DateTimeOffset.UtcNow) { Captures = Captures };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public IReadOnlyList<OwnedPal> ReadGlobalStorage(string accountFolderPath)
        {
            var attempt = Interlocked.Increment(ref GlobalReads);
            Enter();
            try
            {
                if (Failure(attempt) is { } failure) throw failure;
                return Pals(attempt);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        private void Enter()
        {
            var now = Interlocked.Increment(ref _concurrent);
            int seen;
            while (now > (seen = _maxConcurrent)) Interlocked.CompareExchange(ref _maxConcurrent, now, seen);
        }
    }

    private static readonly SyncOptions Fast = new() { Debounce = TimeSpan.FromMilliseconds(300), PollInterval = TimeSpan.FromSeconds(30) };

    private readonly string _dir = TestSupport.NewTempFolder();
    private readonly FakeReader _reader = new();
    private readonly OwnedPalRepository _repo = new(TestSupport.GameData);
    private readonly List<SyncService> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* watcher handles may linger briefly */ }
    }

    private readonly CaptureProgressStore _captures = new();

    private SyncService NewService(SyncOptions? options = null)
    {
        var service = new SyncService(_reader, _repo, options ?? Fast, captures: _captures);
        _services.Add(service);
        return service;
    }

    private string NewWorldFolder(string name = "world")
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(folder, "Players"));
        File.WriteAllBytes(Path.Combine(folder, "Level.sav"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "Players", "p1.sav"), [1]);
        return folder;
    }

    private static OwnedPal MakePal(string uid) =>
        new(uid, null, "CowPal", Gender.Female, 10, 1, [], null, [], [], null,
            new PalOwner("p1", "P1", null, null), new PalLocation(LocationKind.Palbox, null, null, null),
            PalSourceKind.Save, "world", DateTimeOffset.UnixEpoch, false, false);

    private static void Touch(string file) =>
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(2));

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }

        return condition();
    }

    // ---- reading -----------------------------------------------------------------------------------------------

    [Fact]
    public void NewEnabledSource_IsReadAtOnce_AndItsPalsArePublished()
    {
        _reader.Pals = _ => [MakePal("save:w:1"), MakePal("save:w:2")];
        var service = NewService();
        var events = 0;
        service.StatusesChanged += (_, _) => Interlocked.Increment(ref events);

        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, NewWorldFolder(), true)]);

        Assert.True(WaitUntil(() => service.Statuses is [{ State: SourceState.Ok }]));
        var status = Assert.Single(service.Statuses);
        Assert.Equal("src1", status.SourceId);
        Assert.Equal(2, status.PalCount);
        Assert.NotNull(status.LastSyncUtc);
        Assert.Null(status.Error);
        Assert.Equal(2, _repo.Current.Pals.Count);
        Assert.All(_repo.Current.Pals, p => Assert.Equal("src1", p.SourceId));
        Assert.True(events >= 2); // Reading, then Ok
    }

    [Fact]
    public void CaptureRecords_AreKeptPerSource_AndForgottenWithTheSource()
    {
        var record = new CaptureRecord(new Dictionary<string, int> { ["CowPal"] = 5 }, new Dictionary<string, int> { ["CowPal"] = 7 });
        _reader.Captures = [new PlayerCapture("p1", record, DateTimeOffset.UtcNow)];
        var service = NewService();
        var folder = NewWorldFolder();

        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, folder, true)]);

        Assert.True(WaitUntil(() => _captures.Resolve("p1").State == CaptureAvailability.Available));
        Assert.Equal(5, _captures.Resolve("p1").Record!.BonusOf("CowPal"));

        service.ApplyConfig([]);
        Assert.Equal(CaptureAvailability.NoData, _captures.Resolve("p1").State);
    }

    [Fact]
    public void DisabledSource_IsNotRead_UntilEnabled()
    {
        _reader.Pals = _ => [MakePal("save:w:1")];
        var service = NewService();
        var folder = NewWorldFolder();

        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, folder, false)]);
        Thread.Sleep(300);
        Assert.Equal(0, _reader.WorldReads);
        Assert.Equal(SourceState.Idle, Assert.Single(service.Statuses).State);
        Assert.Empty(_repo.Current.Pals);

        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, folder, true)]);
        Assert.True(WaitUntil(() => _repo.Current.Pals.Count == 1));
    }

    [Fact]
    public void GlobalStorageSource_ReadsTheAccountFolder()
    {
        var account = Path.Combine(_dir, "account");
        Directory.CreateDirectory(account);
        var gps = Path.Combine(account, "GlobalPalStorage.sav");
        File.WriteAllBytes(gps, [1]);
        _reader.Pals = _ => [MakePal("gps:1")];
        var service = NewService();

        service.ApplyConfig([new SourceConfig("gps", SourceType.GlobalStorageFile, gps, true)]);

        Assert.True(WaitUntil(() => _repo.Current.Pals.Count == 1));
        Assert.Equal(1, _reader.GlobalReads);
        Assert.Equal(0, _reader.WorldReads);
        Assert.Equal("gps", _repo.Current.Pals.Single().SourceId);
    }

    [Fact]
    public void ManySources_AreReadOneAtATime()
    {
        _reader.Delay = TimeSpan.FromMilliseconds(80);
        var service = NewService();
        service.ApplyConfig(Enumerable.Range(0, 4)
            .Select(i => new SourceConfig("s" + i, SourceType.WorldFolder, NewWorldFolder("w" + i), true)).ToList());

        Assert.True(WaitUntil(() => service.Statuses.All(s => s.State == SourceState.Ok)));
        Assert.Equal(1, _reader.MaxConcurrent);
        Assert.Equal(4, _reader.WorldReads);
    }

    // ---- change detection --------------------------------------------------------------------------------------

    [Fact]
    public void ChangedFile_IsReadAgain_WithinTheDebounceTime()
    {
        var folder = NewWorldFolder();
        var service = NewService();
        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, folder, true)]);
        Assert.True(WaitUntil(() => _reader.WorldReads == 1 && service.Statuses[0].State == SourceState.Ok));

        var stopwatch = Stopwatch.StartNew();
        Touch(Path.Combine(folder, "Level.sav"));

        Assert.True(WaitUntil(() => _reader.WorldReads == 2, 3000), "the changed file was not read again");
        Assert.True(stopwatch.ElapsedMilliseconds < 2500, $"took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ManyQuickChanges_AreCollapsedIntoOneRead()
    {
        var folder = NewWorldFolder();
        var service = NewService();
        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, folder, true)]);
        Assert.True(WaitUntil(() => _reader.WorldReads == 1 && service.Statuses[0].State == SourceState.Ok));

        for (var i = 0; i < 6; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, "Level.sav"), [1, (byte)i]);
            File.WriteAllBytes(Path.Combine(folder, "Players", "p1.sav"), [1, (byte)i]);
            Thread.Sleep(40);
        }

        Assert.True(WaitUntil(() => _reader.WorldReads >= 2, 3000));
        Thread.Sleep(1200); // no further reads follow the burst
        Assert.Equal(2, _reader.WorldReads);
    }

    [Fact]
    public void ChangesInTheBackupFolder_AreIgnored()
    {
        var folder = NewWorldFolder();
        Directory.CreateDirectory(Path.Combine(folder, "backup", "world", "2026"));
        var service = NewService();
        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, folder, true)]);
        Assert.True(WaitUntil(() => _reader.WorldReads == 1 && service.Statuses[0].State == SourceState.Ok));

        File.WriteAllBytes(Path.Combine(folder, "backup", "world", "2026", "Level.sav"), [9, 9]);
        Thread.Sleep(1200);

        Assert.Equal(1, _reader.WorldReads);
    }

    [Fact]
    public void Polling_FindsChangesTheWatcherMissed()
    {
        var folder = NewWorldFolder();
        var service = NewService(new SyncOptions
        {
            Debounce = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(150),
            UseWatchers = false,
        });
        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, folder, true)]);
        Assert.True(WaitUntil(() => _reader.WorldReads == 1 && service.Statuses[0].State == SourceState.Ok));

        Touch(Path.Combine(folder, "Players", "p1.sav"));

        Assert.True(WaitUntil(() => _reader.WorldReads == 2, 3000), "polling did not notice the change");
    }

    [Fact]
    public async Task ZipImportSource_IsNeverWatched()
    {
        var folder = NewWorldFolder();
        var service = NewService();
        service.ApplyConfig([new SourceConfig("zip", SourceType.ZipImport, folder, true)]);
        Assert.True(WaitUntil(() => _reader.WorldReads == 1 && service.Statuses[0].State == SourceState.Ok));

        Touch(Path.Combine(folder, "Level.sav"));
        Thread.Sleep(1200);
        Assert.Equal(1, _reader.WorldReads);

        await service.RefreshNowAsync("zip");
        Assert.Equal(2, _reader.WorldReads);
    }

    // ---- errors ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FailedRead_KeepsThePreviousData_AndShowsTheError()
    {
        var folder = NewWorldFolder();
        _reader.Pals = attempt => [MakePal("save:w:" + attempt)];
        _reader.Failure = attempt => attempt == 2 ? new InvalidDataException("file is corrupt") : null;
        var service = NewService();
        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, folder, true)]);
        Assert.True(WaitUntil(() => service.Statuses[0].State == SourceState.Ok));
        var first = _repo.Current.Pals.Single().Uid;

        await service.RefreshNowAsync();

        var status = Assert.Single(service.Statuses);
        Assert.Equal(SourceState.Error, status.State);
        Assert.Equal("file is corrupt", status.Error);
        Assert.Equal(first, Assert.Single(_repo.Current.Pals).Uid); // previous data survives
        Assert.NotNull(status.LastSyncUtc);                         // last successful sync is still shown
        Assert.Equal(1, status.PalCount);
        Assert.Equal(SourceState.Error, Assert.Single(_repo.Current.Sources).State);

        await service.RefreshNowAsync();

        Assert.Equal(SourceState.Ok, service.Statuses[0].State);
        Assert.Null(service.Statuses[0].Error);
        Assert.Equal("save:w:3", Assert.Single(_repo.Current.Pals).Uid);
    }

    [Fact]
    public void WorldHostedElsewhere_ReportsTheLocalizationKey()
    {
        _reader.Failure = _ => WorldUnavailableException.HostedElsewhere("x");
        var service = NewService();
        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, NewWorldFolder(), true)]);

        Assert.True(WaitUntil(() => service.Statuses[0].State == SourceState.Error));
        Assert.Equal(WorldUnavailableException.HostedElsewhereKey, service.Statuses[0].Error);
    }

    [Fact]
    public void MissingFolder_IsAnError_NotACrash()
    {
        _reader.Failure = _ => new DirectoryNotFoundException("World folder not found");
        var service = NewService();
        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, Path.Combine(_dir, "nowhere"), true)]);

        Assert.True(WaitUntil(() => service.Statuses[0].State == SourceState.Error));
        Assert.Empty(_repo.Current.Pals);
    }

    // ---- refresh and configuration -----------------------------------------------------------------------------

    [Fact]
    public async Task RefreshNow_ReadsImmediately_EvenWithALongDebounce()
    {
        var folder = NewWorldFolder();
        var service = NewService(new SyncOptions { Debounce = TimeSpan.FromMinutes(5), PollInterval = TimeSpan.FromMinutes(5) });
        service.ApplyConfig([new SourceConfig("src1", SourceType.WorldFolder, folder, true)]);
        Assert.True(WaitUntil(() => service.Statuses[0].State == SourceState.Ok));

        Touch(Path.Combine(folder, "Level.sav"));
        await service.RefreshNowAsync("src1");

        Assert.Equal(2, _reader.WorldReads);
        await service.RefreshNowAsync("unknown-id"); // no-op
        Assert.Equal(2, _reader.WorldReads);
        await service.RefreshNowAsync();             // all sources
        Assert.Equal(3, _reader.WorldReads);
    }

    [Fact]
    public void RemovedSource_IsForgotten_AndItsPalsDisappear()
    {
        _reader.Pals = _ => [MakePal("save:w:1")];
        var service = NewService();
        var a = new SourceConfig("a", SourceType.WorldFolder, NewWorldFolder("a"), true);
        var b = new SourceConfig("b", SourceType.WorldFolder, NewWorldFolder("b"), true);
        service.ApplyConfig([a, b]);
        Assert.True(WaitUntil(() => service.Statuses.All(s => s.State == SourceState.Ok)));

        service.ApplyConfig([b]);

        Assert.Equal(["b"], service.Statuses.Select(s => s.SourceId));
        Assert.Equal(["b"], _repo.Current.Pals.Select(p => p.SourceId));
    }

    [Fact]
    public void ChangedPath_ReadsTheNewLocation()
    {
        var service = NewService();
        service.ApplyConfig([new SourceConfig("a", SourceType.WorldFolder, NewWorldFolder("one"), true)]);
        Assert.True(WaitUntil(() => _reader.WorldReads == 1 && service.Statuses[0].State == SourceState.Ok));

        var second = NewWorldFolder("two");
        service.ApplyConfig([new SourceConfig("a", SourceType.WorldFolder, second, true)]);

        Assert.True(WaitUntil(() => _reader.WorldReads == 2));
        Assert.Equal(second, _reader.WorldPaths[^1]);
    }

    [Fact]
    public void UnchangedConfig_IsNotReadAgain()
    {
        var service = NewService();
        var config = new SourceConfig("a", SourceType.WorldFolder, NewWorldFolder(), true);
        service.ApplyConfig([config]);
        Assert.True(WaitUntil(() => _reader.WorldReads == 1 && service.Statuses[0].State == SourceState.Ok));

        service.ApplyConfig([config]);
        Thread.Sleep(300);
        Assert.Equal(1, _reader.WorldReads);
    }

    [Fact]
    public void StatusesFollowTheConfigurationOrder()
    {
        var service = NewService();
        var configs = new[] { "z", "a", "m" }
            .Select(id => new SourceConfig(id, SourceType.WorldFolder, NewWorldFolder(id), false)).ToList();
        service.ApplyConfig(configs);
        Assert.Equal(["z", "a", "m"], service.Statuses.Select(s => s.SourceId));
    }

    [Fact]
    public void AfterDispose_NothingIsReadAnymore()
    {
        var folder = NewWorldFolder();
        var service = NewService();
        service.ApplyConfig([new SourceConfig("a", SourceType.WorldFolder, folder, true)]);
        Assert.True(WaitUntil(() => _reader.WorldReads == 1 && service.Statuses[0].State == SourceState.Ok));

        service.Dispose();
        Touch(Path.Combine(folder, "Level.sav"));
        Thread.Sleep(800);

        Assert.Equal(1, _reader.WorldReads);
    }
}
