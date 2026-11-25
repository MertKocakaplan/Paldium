using Paldium.Core.Owned;
using Paldium.Import.Bridge;
using Paldium.Import.Saves;

namespace Paldium.Import.Tests;

public class PlayerCaptureReaderTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempFolder();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Normalize_TurnsNamesIntoBundleIds_AndLogsTheUnknownOnes()
    {
        var log = new List<string>();
        var raw = new CaptureRecord(
            new Dictionary<string, int> { ["Sheepball"] = 5, ["no_such_species"] = 3, ["ChickenPal"] = 2 },
            new Dictionary<string, int> { ["Sheepball"] = 7 });

        var record = PlayerCaptureReader.Normalize(raw, TestSupport.GameData, (m, _) => log.Add(m));

        Assert.Equal(5, record.BonusOf("SheepBall"));
        Assert.Equal(2, record.BonusOf("ChickenPal"));
        Assert.Equal(7, record.CountOf("SheepBall"));
        Assert.Equal(0, record.BonusOf("no_such_species"));
        var line = Assert.Single(log);
        Assert.Contains("no_such_species", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_ClampsBonusToFive_AndMissingSpeciesAreZero()
    {
        var record = new CaptureRecord(new Dictionary<string, int> { ["A"] = 9, ["B"] = -2 }, new Dictionary<string, int> { ["A"] = 9 });
        Assert.Equal(5, record.BonusOf("A"));
        Assert.Equal(0, record.BonusOf("B"));
        Assert.Equal(0, record.BonusOf("C"));
        Assert.Equal(9, record.CountOf("A"));
    }

    [Fact]
    public void ModFile_WithACaptureBlock_GivesTheOwnPlayersRecord()
    {
        var file = Path.Combine(_dir, "palbox.json");
        File.WriteAllText(file, """
            { "schemaVersion": 1, "exportedAtUtc": "2026-11-20T10:00:00Z", "playerUid": "AABBCCDD-0000-0000-0000-000000000001", "pals": [],
              "capture": { "bonus": { "Sheepball": 5, "Kitsunebi": 4 }, "count": { "Sheepball": 7, "Kitsunebi": 4 } } }
            """);

        var capture = Assert.Single(new ModBridgeReader(TestSupport.GameData).ReadCaptures(file));

        Assert.Equal("AABBCCDD-0000-0000-0000-000000000001", capture.PlayerId);
        Assert.Equal(5, capture.Record.BonusOf("SheepBall"));
        Assert.Equal(4, capture.Record.BonusOf("Kitsunebi"));
        Assert.Equal(7, capture.Record.CountOf("SheepBall"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:00:00Z"), capture.ReadAtUtc);
    }

    [Fact]
    public void ModFile_WithoutCapture_OrWithoutPlayer_GivesNothing()
    {
        var file = Path.Combine(_dir, "palbox.json");
        File.WriteAllText(file, """{ "schemaVersion": 1, "playerUid": "AABBCCDD-0000-0000-0000-000000000001", "pals": [] }""");
        Assert.Empty(new ModBridgeReader(TestSupport.GameData).ReadCaptures(file));

        File.WriteAllText(file, """{ "schemaVersion": 1, "pals": [], "capture": { "bonus": { "Sheepball": 5 } } }""");
        Assert.Empty(new ModBridgeReader(TestSupport.GameData).ReadCaptures(file));
    }

    [Fact]
    public void BuildRelics_JoinsTheHeldAndCollectedCounts_AndIsNullWithoutBoth()
    {
        Assert.Null(PlayerCaptureReader.BuildRelics(null, null));

        var relics = PlayerCaptureReader.BuildRelics(
            new Dictionary<string, int> { ["MoveSpeed"] = 1, ["CapturePower"] = 2 },
            new Dictionary<string, int> { ["CapturePower"] = 66, ["HungerReduction"] = 26 })!;

        Assert.Equal(new RelicCount(2, 66), relics["CapturePower"]);
        Assert.Equal(new RelicCount(1, 0), relics["MoveSpeed"]);          // held, never collected from the world
        Assert.Equal(new RelicCount(0, 26), relics["HungerReduction"]);   // collected, none in hand: the save leaves a zero out
        Assert.Equal("MoveSpeed", PlayerCaptureReader.RelicTypeName("EPalRelicType::MoveSpeed"));
        Assert.Equal("MoveSpeed", PlayerCaptureReader.RelicTypeName("MoveSpeed"));
    }

    [Fact]
    public void ModFile_WithARelicsBlock_GivesTheEffigiesOfTheOwnPlayer_EvenWithoutACaptureBlock()
    {
        var file = Path.Combine(_dir, "palbox.json");
        File.WriteAllText(file, """
            { "schemaVersion": 1, "exportedAtUtc": "2026-11-21T10:00:00Z", "playerUid": "AABBCCDD-0000-0000-0000-000000000001", "pals": [],
              "relics": { "CapturePower": { "have": 2, "collected": 66 }, "MoveSpeed": { "have": 1 }, "HungerReduction": { "collected": 26 } } }
            """);

        var capture = Assert.Single(new ModBridgeReader(TestSupport.GameData).ReadCaptures(file));

        Assert.Equal(new RelicCount(2, 66), capture.Record.Relics!["CapturePower"]);
        Assert.Equal(new RelicCount(1, null), capture.Record.Relics["MoveSpeed"]);
        Assert.Equal(new RelicCount(null, 26), capture.Record.Relics["HungerReduction"]);
        Assert.False(capture.Record.IsEmpty);
        Assert.True(capture.Record.Bonus.Count == 0);
    }

    [Fact]
    public void Normalize_KeepsTheEffigies()
    {
        var raw = new CaptureRecord(new Dictionary<string, int>(), new Dictionary<string, int>()) { Relics = new Dictionary<string, RelicCount> { ["JumpPower"] = new(3, 5) } };
        Assert.Equal(new RelicCount(3, 5), PlayerCaptureReader.Normalize(raw, TestSupport.GameData, null).Relics!["JumpPower"]);
    }

    [Fact]
    public void BuildRelics_IsCaseInsensitive()
    {
        var relics = PlayerCaptureReader.BuildRelics(
            new Dictionary<string, int> { ["capturepower"] = 4 },
            new Dictionary<string, int> { ["CapturePower"] = 60 })!;

        Assert.Single(relics);
        Assert.Equal(new RelicCount(4, 60), relics["CapturePower"]);
    }

    [Fact]
    public void ModFile_ZeroCaptureBlock_StillPublishesCaptureRecord_AndHandlesDuplicateOrNullRelics_AndClampsFutureTimestamp()
    {
        var file = Path.Combine(_dir, "palbox.json");
        File.WriteAllText(file, """
            { "schemaVersion": 1, "exportedAtUtc": "2099-01-01T00:00:00Z", "playerUid": "AABBCCDD-0000-0000-0000-000000000001", "pals": [],
              "capture": { "bonus": {}, "count": {} },
              "relics": { "CapturePower": null, "EPalRelicType::JumpPower": { "collected": 4 }, "JumpPower": { "have": 1, "collected": 5 } } }
            """);

        var (pals, captures, _) = new ModBridgeReader(TestSupport.GameData).ReadAll(file, "s");
        Assert.Empty(pals);
        var capture = Assert.Single(captures);
        Assert.True(capture.ReadAtUtc <= DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(new RelicCount(1, 5), capture.Record.Relics!["JumpPower"]);
        Assert.False(capture.Record.Relics.ContainsKey("CapturePower"));
    }

    // ---- collected objects (T144) -----------------------------------------------------------------------------

    private static PlayerCapture Player(string id, WorldProgress progress, int minutes = 0, bool local = false) =>
        new(id, CaptureRecord.Empty with { Progress = progress }, DateTimeOffset.UnixEpoch.AddMinutes(minutes), local);

    private static WorldProgress Taken(params string[] ids) => new() { Objects = WorldProgress.Set(ids.Select(CaptureRecord.ObjectKey)) };

    [Fact]
    public void Progress_IsThatOfTheChosenSourcesAndPlayer()
    {
        var store = new CaptureProgressStore();
        store.Publish("save", [Player("p1", Taken("aaaa-bbbb")), Player("p2", Taken("CCCC"))]);
        store.Publish("older", [Player("p1", Taken("DDDD"))]);
        store.Publish("elsewhere", [Player("p1", Taken("EEEE"))]);

        Assert.Equal(["AAAABBBB", "DDDD"], store.Progress(["save", "older"], "p1")!.Objects.Order());
        Assert.Null(store.Progress(["save"], null)); // several players and nobody said which one they are
        Assert.Equal(["DDDD"], store.Progress(["older"], null)!.Objects);
        Assert.Null(store.Progress([], "p1"));
        Assert.Equal(["AAAABBBB", "DDDD", "EEEE"], store.Resolve("p1").Record!.Progress!.Objects.Order());
    }

    [Fact]
    public void WithoutAChosenPlayer_TheModsPlayerIsMe()
    {
        var store = new CaptureProgressStore();
        store.Publish("save", [Player("host", Taken("AAAA")), Player("me", Taken("BBBB"))]);
        store.Publish("mod", [Player("ME", Taken("CCCC"), minutes: 5, local: true)]);

        Assert.Equal(["BBBB", "CCCC"], store.Progress(["save", "mod"], null)!.Objects.Order());
        Assert.Equal(["AAAA"], store.Progress(["save", "mod"], "host")!.Objects);
    }

    [Fact]
    public void Quests_AreTheNewestSources_AndAQuestDoneAnywhereIsNotActive()
    {
        static WorldProgress Quests(string[] done, params (string Id, int Block)[] active) => new()
        {
            Quests = new QuestProgress(active.ToDictionary(a => a.Id, a => a.Block), WorldProgress.Set(done)),
        };
        var store = new CaptureProgressStore();
        store.Publish("save", [Player("p", Quests(["Sub_A"], ("Sub_B", 0), ("Sub_C", 1)))]);
        store.Publish("mod", [Player("p", Quests(["Sub_B"], ("Sub_C", 2), ("Sub_A", 0)), minutes: 5)]);

        var quests = store.Progress(["save", "mod"], null)!.Quests!;

        Assert.Equal(["Sub_A", "Sub_B"], quests.Done.Order());
        var active = Assert.Single(quests.Active);
        Assert.Equal(("Sub_C", 2), (active.Key, active.Value));
    }

    [Fact]
    public void Bases_AreOneEach_TheModsPlaceWins_AndOtherGuildsAreLeftOut()
    {
        var store = new CaptureProgressStore();
        store.Publish("save", [Player("me", Taken("AAAA")), Player("other", Taken("BBBB"))]);
        store.PublishBases("save", [new BaseSite("aaaa-0001", 10, 20) { Members = ["me"] }, new BaseSite("aaaa-0002", 5, 5) { Members = ["other"] }]);
        store.PublishBases("mod", [new BaseSite("AAAA0001", 11, 21, 3500, "Home", 15)]);

        var site = Assert.Single(store.Bases(["save", "mod"], "me"));

        Assert.Equal("AAAA0001", site.Id);
        Assert.Equal((11.0, 21.0, 3500.0), (site.X, site.Y, site.Range!.Value));
        Assert.Equal(("Home", 15), (site.Name, site.Capacity!.Value));
        Assert.Equal(2, store.Bases(["save"], null).Count); // nobody knows who "me" is: every base of the world
        store.Remove("mod");
        Assert.Null(Assert.Single(store.Bases(["save", "mod"], "me")).Range);
    }

    [RealSaveFact(TestSupport.WorldVariable)]
    public void RealPlayers_QuestsJournalsAndBossKeys_AreTheOnesOfTheMap()
    {
        var map = Paldium.Core.Map.MapDataLoader.Load(Path.Combine(TestSupport.RepoRoot, "data"));
        var progress = Directory.GetFiles(Path.Combine(TestSupport.WorldFolder!, "Players"), "*.sav")
            .Where(f => !f.EndsWith("_dps.sav", StringComparison.OrdinalIgnoreCase))
            .Select(f => PlayerCaptureReader.Read(f, TestSupport.GameData)?.Progress).OfType<WorldProgress>().ToList();
        var quests = progress.Where(p => p.Quests != null).SelectMany(p => p.Quests!.Done.Concat(p.Quests.Active.Keys)).ToList();
        var notes = progress.SelectMany(p => p.Notes).ToList();

        Assert.Contains(quests, q => map.TryGetQuest(q) != null);
        var rows = map.Points.Where(p => p.Category == "journal").Select(p => p.Extra!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(notes.Count(rows.Contains) >= notes.Count * 0.5, $"{notes.Count(rows.Contains)} of {notes.Count} journal keys are markers");
        var keys = map.Points.Where(p => p.Category == "boss_tower").Select(p => p.RefId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(progress.SelectMany(p => p.Towers), t => Assert.Contains(t, keys));
    }

    [RealSaveFact(TestSupport.WorldVariable)]
    public void RealPlayers_CollectedObjects_MatchTheEffigyAndTowerMarkers()
    {
        var map = Paldium.Core.Map.MapDataLoader.Load(Path.Combine(TestSupport.RepoRoot, "data"));
        var markers = map.Points.Where(p => p.Category is "effigy" or "pickup_tower" or "fast_travel" or "watchtower")
            .SelectMany(p => new[] { p.Id, p.Category == "pickup_tower" ? p.Extra! : p.Id })
            .Select(CaptureRecord.ObjectKey).ToHashSet();
        var files = Directory.GetFiles(Path.Combine(TestSupport.WorldFolder!, "Players"), "*.sav")
            .Where(f => !f.EndsWith("_dps.sav", StringComparison.OrdinalIgnoreCase)).ToList();
        var taken = files.SelectMany(f => PlayerCaptureReader.Read(f, TestSupport.GameData)?.Progress?.Objects ?? WorldProgress.Set([])).ToList();

        Assert.NotEmpty(taken);
        // Objects of a later game build may be missing; most keys must be markers, else the save spells them differently.
        Assert.True(taken.Count(markers.Contains) >= taken.Count * 0.9, $"{taken.Count(markers.Contains)} of {taken.Count} keys are markers");
    }

    // ---- real saves (skipped unless PALDIUM_TEST_WORLD is set) ------------------------------------------------

    [RealSaveFact(TestSupport.WorldVariable)]
    public void RealPlayers_HaveACaptureRecord_WithBonusFromZeroToFive()
    {
        var players = Directory.GetFiles(Path.Combine(TestSupport.WorldFolder!, "Players"), "*.sav")
            .Where(f => !f.EndsWith("_dps.sav", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(players);
        foreach (var file in players)
        {
            var record = PlayerCaptureReader.Read(file, TestSupport.GameData);
            Assert.NotNull(record);
            Assert.NotEmpty(record.Bonus);
            Assert.All(record.Bonus.Values, v => Assert.InRange(v, 0, 5));
            Assert.All(record.Bonus.Keys, id => Assert.True(TestSupport.GameData.TryNormalizePalId(id, out _), id));
        }
    }

    [RealSaveFact(TestSupport.WorldVariable)]
    public void RealPlayers_HaveEffigies_WhoseSpentCountSitsOnARankCost()
    {
        // spent = collected - in hand (T133): for every type the player has started it must equal the cumulative cost of a rank (or the total).
        var players = Directory.GetFiles(Path.Combine(TestSupport.WorldFolder!, "Players"), "*.sav")
            .Where(f => !f.EndsWith("_dps.sav", StringComparison.OrdinalIgnoreCase)).ToList();
        var checkedTypes = 0;
        foreach (var file in players)
        {
            var relics = PlayerCaptureReader.Read(file, TestSupport.GameData)?.Relics;
            if (relics == null) continue;
            foreach (var (type, count) in relics)
            {
                if (type == "MoveSpeed" || count.Have is not { } have || count.Collected is not { } collected) continue;
                var info = TestSupport.GameData.Relics.Single(r => r.Type == type);
                var cumulative = info.Ranks.OrderBy(r => r.Rank).Select(r => r.Required).Aggregate(new List<int> { 0 }, (list, n) => { list.Add(list[^1] + n); return list; });
                var spent = collected - have;
                Assert.True(cumulative.Contains(spent), $"{type}: collected {collected} - held {have} = {spent} is not a rank cost");
                checkedTypes++;
            }
        }

        Assert.True(checkedTypes > 0);
    }

    [RealSaveFact(TestSupport.WorldVariable)]
    public void RealWorld_ReadWorld_ReturnsTheCaptureRecordOfEveryPlayer()
    {
        var snapshot = new SaveWorldReader(TestSupport.GameData, Path.Combine(_dir, "work")).ReadWorld(TestSupport.WorldFolder!);
        Assert.NotEmpty(snapshot.Captures);
        Assert.All(snapshot.Captures, c => Assert.Contains(snapshot.Players, p => p.PlayerId == c.PlayerId));
    }
}
