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
