using System.IO.Compression;
using Paldium.App.Services;
using Paldium.Core.Owned;
using Paldium.Import.Sync;

namespace Paldium.App.Tests;

public class SourcesServiceTests : IDisposable
{
    private sealed class FakeSync : ISyncService
    {
        public readonly List<IReadOnlyList<SourceConfig>> Applied = [];
        public readonly List<string?> Refreshed = [];
        public IReadOnlyList<SourceStatus> Statuses => [];
        public event EventHandler? StatusesChanged { add { } remove { } }

        public Task RefreshNowAsync(string? sourceId = null)
        {
            Refreshed.Add(sourceId);
            return Task.CompletedTask;
        }

        public void ApplyConfig(IReadOnlyList<SourceConfig> sources) => Applied.Add(sources.ToList());

        public void Dispose() { }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-sources-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSync _sync = new();
    private readonly SettingsStore _settings;
    private readonly ZipImporter _zip;
    private readonly List<string> _accounts = [];

    public SourcesServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _zip = new ZipImporter(Path.Combine(_dir, "imports"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string BridgeFile => Path.Combine(_dir, "bridge", "palbox.json");

    private SourcesService NewService() => new(_settings, _sync, _zip, () => _accounts, bridgeFile: BridgeFile);

    private void WriteBridgeFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BridgeFile)!);
        File.WriteAllText(BridgeFile, "{ \"schemaVersion\": 1, \"pals\": [] }");
    }

    private string NewAccountWithGlobalPalbox()
    {
        var account = Path.Combine(_dir, "account-" + _accounts.Count);
        Directory.CreateDirectory(account);
        File.WriteAllBytes(Path.Combine(account, "GlobalPalStorage.sav"), [1]);
        _accounts.Add(account);
        return Path.Combine(account, "GlobalPalStorage.sav");
    }

    private string NewWorld(string name)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Level.sav"), [1]);
        return folder;
    }

    [Fact]
    public void FirstRun_DoesNotAddTheGlobalPalbox_AndItIsOutOfTheDefaultScope()
    {
        NewAccountWithGlobalPalbox();
        var service = NewService();

        service.Start();

        Assert.Empty(service.Sources);
        Assert.True(_settings.Current.GlobalPalboxMigrated);
        Assert.DoesNotContain(Paldium.Core.Domain.LocationKind.GlobalStorage, _settings.Current.Scope.Locations);
        Assert.Contains(Paldium.Core.Domain.LocationKind.DimensionalStorage, _settings.Current.Scope.Locations);
        Assert.DoesNotContain(Paldium.Core.Domain.LocationKind.GlobalStorage, new ScopeSettings().Locations);
    }

    [Fact]
    public void OldAutomaticGlobalPalbox_IsRemovedOnce_ButAManualOneAddedLaterStays()
    {
        var gps = NewAccountWithGlobalPalbox();
        var elsewhere = Path.Combine(_dir, "elsewhere", "GlobalPalStorage.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllBytes(elsewhere, [1]);
        var world = NewWorld("w1");
        // The state an older version left: the automatic local Global Palbox, a world, and GlobalStorage in the scope.
        _settings.Update(s => s with
        {
            SourcesInitialized = true,
            Sources =
            [
                new SourceConfig("auto", SourceType.GlobalStorageFile, Path.GetFullPath(gps), true),
                new SourceConfig("other", SourceType.GlobalStorageFile, Path.GetFullPath(elsewhere), true),
                new SourceConfig("world", SourceType.WorldFolder, world, true),
            ],
            Scope = s.Scope with { Locations = [.. Enum.GetValues<Paldium.Core.Domain.LocationKind>()] },
        });

        NewService().Start();

        Assert.Equal(["other", "world"], _settings.Current.Sources.Select(s => s.Id));
        Assert.DoesNotContain(Paldium.Core.Domain.LocationKind.GlobalStorage, _settings.Current.Scope.Locations);

        // It runs only once: a Global Palbox the user adds by hand afterwards (and puts back in the scope) is not touched.
        var service = NewService();
        service.AddLocalGlobalPalboxes();
        _settings.Update(s => s with { Scope = s.Scope with { Locations = [.. s.Scope.Locations, Paldium.Core.Domain.LocationKind.GlobalStorage] } });
        NewService().Start();
        Assert.Contains(_settings.Current.Sources, s => s.Type == SourceType.GlobalStorageFile && s.Path == Path.GetFullPath(gps));
        Assert.Contains(Paldium.Core.Domain.LocationKind.GlobalStorage, _settings.Current.Scope.Locations);
    }
    [Fact]
    public void BridgeFile_IsAddedAutomaticallyOnce_AndStaysRemovedAfterTheUserRemovesIt()
    {
        var service = NewService();
        service.Start();
        Assert.Empty(service.Sources); // no file yet, nothing offered
        Assert.False(service.BridgeSuggested);

        WriteBridgeFile();
        service = NewService();
        service.Start();
        var source = Assert.Single(service.Sources);
        Assert.Equal(SourceType.ModBridgeFile, source.Type);
        Assert.Equal(Path.GetFullPath(BridgeFile), source.Path);
        Assert.True(source.Enabled);

        service.Remove(source.Id);
        service = NewService();
        service.Start();
        Assert.Empty(service.Sources);
        Assert.True(service.BridgeSuggested); // offered as a button, not added again

        service.AddBridgeFile();
        Assert.Single(service.Sources);
        Assert.False(service.BridgeSuggested);
    }

    [Fact]
    public void Start_AppliesTheSavedSourcesToTheSyncService()
    {
        var service = NewService();
        service.AddWorldFolder(NewWorld("w1"));
        _sync.Applied.Clear();

        var restarted = new SourcesService(new SettingsStore(Path.Combine(_dir, "settings.json")), _sync, _zip, () => [], bridgeFile: BridgeFile);
        restarted.Start();

        Assert.Single(restarted.Sources);
        Assert.Single(_sync.Applied[^1]);
    }

    [Fact]
    public void Sources_AreStoredInTheSettingsFile()
    {
        var service = NewService();
        var added = service.AddWorldFolder(NewWorld("w1"));

        var text = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.Contains("\"type\": \"WorldFolder\"", text);

        var loaded = Assert.Single(new SettingsStore(Path.Combine(_dir, "settings.json")).Current.Sources);
        Assert.Equal(added, loaded);
    }

    [Fact]
    public void AddWorldFolder_AcceptsTheWorldFolderOrAParent_AndRejectsOthers()
    {
        var service = NewService();
        var world = NewWorld(Path.Combine("sync", "MyWorld"));

        var viaParent = service.AddWorldFolder(Path.Combine(_dir, "sync"));
        Assert.Equal(Path.GetFullPath(world), viaParent.Path);

        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(SourceException.NoWorld, Assert.Throws<SourceException>(() => service.AddWorldFolder(empty)).Key);
        Assert.Equal(SourceException.NoWorld, Assert.Throws<SourceException>(() => service.AddWorldFolder(Path.Combine(_dir, "missing"))).Key);
    }

    [Fact]
    public void AddingTheSamePathTwice_ReturnsTheExistingSource()
    {
        var service = NewService();
        var world = NewWorld("w1");

        var first = service.AddWorldFolder(world);
        var second = service.AddWorldFolder(world.ToUpperInvariant() + Path.DirectorySeparatorChar);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(service.Sources);
        Assert.True(service.IsKnown(SourceType.WorldFolder, world));
    }

    [Fact]
    public void AddZip_ExtractsTheWorld_AndRemovingTheSourceDeletesTheCopy()
    {
        var zipPath = Path.Combine(_dir, "friend.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("World/Level.sav").Open().Dispose();
            archive.CreateEntry("World/Players/a.sav").Open().Dispose();
        }

        var service = NewService();
        var source = service.AddZip(zipPath);

        Assert.Equal(SourceType.ZipImport, source.Type);
        Assert.True(File.Exists(Path.Combine(source.Path, "Level.sav")));
        Assert.StartsWith(Path.Combine(_dir, "imports"), source.Path, StringComparison.OrdinalIgnoreCase);

        service.Remove(source.Id);

        Assert.Empty(service.Sources);
        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "imports")));
    }

    [Fact]
    public void AddZip_WithoutAWorld_ReportsAnError()
    {
        var zipPath = Path.Combine(_dir, "bad.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            archive.CreateEntry("readme.txt").Open().Dispose();

        var ex = Assert.Throws<SourceException>(() => NewService().AddZip(zipPath));
        Assert.Equal(SourceException.ZipFailed, ex.Key);
    }

    [Fact]
    public void EnableDisableAndRemove_UpdateTheSyncServiceEveryTime()
    {
        var service = NewService();
        var source = service.AddWorldFolder(NewWorld("w1"));
        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.SetEnabled(source.Id, false);
        Assert.False(Assert.Single(_sync.Applied[^1]).Enabled);

        service.SetEnabled(source.Id, true);
        Assert.True(Assert.Single(_sync.Applied[^1]).Enabled);

        service.Remove(source.Id);
        Assert.Empty(_sync.Applied[^1]);
        Assert.Equal(3, raised);
    }

    [Fact]
    public void GlobalPalbox_DetectionSkipsKnownFiles_AndPicksFilesManually()
    {
        var gps = NewAccountWithGlobalPalbox();
        var service = NewService();

        Assert.Equal([gps], service.FindLocalGlobalPalboxFiles());
        Assert.Single(service.AddLocalGlobalPalboxes());
        Assert.Empty(service.FindLocalGlobalPalboxFiles());
        Assert.Empty(service.AddLocalGlobalPalboxes());

        var other = Path.Combine(_dir, "elsewhere", "GlobalPalStorage.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, [1]);
        Assert.Equal(SourceType.GlobalStorageFile, service.AddGlobalStorageFile(other).Type);
        Assert.Equal(SourceException.NoGlobal,
            Assert.Throws<SourceException>(() => service.AddGlobalStorageFile(Path.Combine(_dir, "nope.sav"))).Key);
    }

    [Fact]
    public async Task Refresh_IsForwardedToTheSyncService()
    {
        var service = NewService();
        await service.RefreshAsync();
        await service.RefreshAsync("abc");
        Assert.Equal([null, "abc"], _sync.Refreshed);
    }
}
