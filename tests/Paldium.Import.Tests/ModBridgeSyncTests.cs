using System.Diagnostics;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Import.Bridge;
using Paldium.Import.Saves;
using Paldium.Import.Sync;

namespace Paldium.Import.Tests;

/// <summary>The bridge file as a watched source: it follows changes, keeps the old data on a broken file and merges with a save.</summary>
public class ModBridgeSyncTests : IDisposable
{
    private sealed class NoSaves : ISaveWorldReader
    {
        public IReadOnlyList<DiscoveredWorld> DiscoverLocalWorlds() => [];
        public WorldSnapshot ReadWorld(string worldFolderPath) => throw new NotSupportedException();
        public IReadOnlyList<OwnedPal> ReadGlobalStorage(string accountFolderPath) => [];
    }

    private static readonly SyncOptions Fast = new() { Debounce = TimeSpan.FromMilliseconds(200), PollInterval = TimeSpan.FromSeconds(30) };

    private readonly string _dir = TestSupport.NewTempFolder();
    private readonly OwnedPalRepository _repo = new(TestSupport.GameData);
    private readonly SyncService _service;
    private readonly string _file;

    public ModBridgeSyncTests()
    {
        _service = new SyncService(new NoSaves(), _repo, Fast, bridge: new ModBridgeReader(TestSupport.GameData));
        _file = Path.Combine(_dir, "palbox.json");
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* watcher handles may linger briefly */ }
    }

    private static string Json(params string[] pals) =>
        "{ \"schemaVersion\": 1, \"pals\": [" + string.Join(",", pals) + "] }";

    private static string Pal(string instanceId, string species = "CowPal") =>
        $"{{ \"instanceId\": \"{instanceId}\", \"characterId\": \"{species}\", \"location\": {{ \"kind\": \"Palbox\", \"page\": 0, \"slot\": 0 }} }}";

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 8000)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }

        return condition();
    }

    [Fact]
    public void FileChanges_AreFollowed_AndABrokenFileKeepsThePreviousPals()
    {
        File.WriteAllText(_file, Json(Pal("AAA1")));
        _service.ApplyConfig([new SourceConfig("bridge", SourceType.ModBridgeFile, _file, true)]);
        Assert.True(WaitUntil(() => _repo.Current.Pals.Count == 1));

        // The mod writes a new export: another Pal appears without any key press in the app.
        File.WriteAllText(_file, Json(Pal("AAA1"), Pal("BBB2", "Sekhmet")));
        Assert.True(WaitUntil(() => _repo.Current.Pals.Count == 2), "the new export was not picked up");

        // A half written file is an error; the Pals stay.
        File.WriteAllText(_file, "{ \"schemaVersion\": 1, \"pals\": [ ");
        Assert.True(WaitUntil(() => _service.Statuses is [{ State: SourceState.Error }]));
        Assert.Equal(2, _repo.Current.Pals.Count);
    }

    [Fact]
    public void ThePalFromASaveAndFromTheMod_AreOnePal()
    {
        var save = new OwnedPal("save:w:1", "CCC3", "CowPal", Gender.Female, 10, 1, [], null, [], [], null,
            new PalOwner("p1", "P1", null, null), new PalLocation(LocationKind.Palbox, null, null, null),
            PalSourceKind.Save, "world", DateTimeOffset.UnixEpoch, false, false);
        _repo.PublishSourceSnapshot("world", [save], new SourceStatus("world", SourceState.Ok, DateTimeOffset.UtcNow, 1, null));

        File.WriteAllText(_file, Json(Pal("CCC3")));
        _service.ApplyConfig([new SourceConfig("bridge", SourceType.ModBridgeFile, _file, true)]);

        Assert.True(WaitUntil(() => _repo.Current.Sources.Any(s => s.SourceId == "bridge" && s.State == SourceState.Ok)));
        var pal = Assert.Single(_repo.Current.Pals);
        Assert.Equal(1, _repo.Current.DuplicateCount);
        Assert.Equal("CCC3", pal.InstanceId);
    }
}
