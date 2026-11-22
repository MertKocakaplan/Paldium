using Paldium.Core.Domain;
using Paldium.Import.Bridge;

namespace Paldium.Import.Tests;

public class ModBridgeReaderTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempFolder();
    private readonly List<string> _log = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ModBridgeReader NewReader() => new(TestSupport.GameData, (m, _) => _log.Add(m));

    private string Write(string json)
    {
        var file = Path.Combine(_dir, "palbox.json");
        File.WriteAllText(file, json);
        return file;
    }

    private static string ExampleFile() => Path.Combine(AppContext.BaseDirectory, "TestData", "palbox.example.json");

    [Fact]
    public void ExampleFile_IsConvertedWithUidsOwnerAndLocation()
    {
        var pals = NewReader().Read(ExampleFile(), "src1");
        Assert.Equal(5, pals.Count);

        var first = pals[0];
        Assert.Equal("mod:11111111222222223333333344444444", first.Uid);
        Assert.Equal("11111111222222223333333344444444", first.InstanceId);
        Assert.Equal("CowPal", first.SpeciesId);
        Assert.Equal(Gender.Female, first.Gender);
        Assert.Equal(new Ivs(70, 55, 80), first.Ivs);
        Assert.Equal(PalSourceKind.ModBridge, first.Source);
        Assert.Equal("src1", first.SourceId);
        Assert.Equal(LocationKind.Palbox, first.Location.Kind);
        Assert.Equal(0, first.Location.SlotIndex);
        Assert.All(pals, p => Assert.StartsWith("mod:", p.Uid));
        Assert.Equal(pals.Count, pals.Select(p => p.Uid).Distinct().Count());
    }

    [Fact]
    public void BossPrefix_IsMappedToTheSpecies()
    {
        var pals = NewReader().Read(ExampleFile(), "s");
        Assert.Contains(pals, p => p.SpeciesId == "Anubis");
        Assert.DoesNotContain(pals, p => p.SpeciesId.StartsWith("BOSS_", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PalWithoutInstanceId_GetsAPositionUid()
    {
        var file = Write("""{ "schemaVersion": 1, "pals": [ { "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 2, "slot": 5 } } ] }""");
        var pal = Assert.Single(NewReader().Read(file, "s"));
        Assert.Equal("mod:DimensionalStorage:2:5", pal.Uid);
        Assert.Null(pal.InstanceId);
        Assert.Equal(LocationKind.DimensionalStorage, pal.Location.Kind);
        Assert.Null(pal.Ivs);
        Assert.Equal(1, pal.Level);
    }

    [Fact]
    public void UnknownSpeciesPassivesAndAttacks_AreDroppedAndLogged()
    {
        var file = Write("""
            { "schemaVersion": 1, "pals": [
              { "characterId": "NoSuchPal" },
              { "characterId": "CowPal", "passives": ["CraftSpeed_up2", "NoSuchPassive"], "equippedAttacks": ["AirBlade", "EPalWazaID::AirCanon", "NoSuchAttack"] } ] }
            """);
        var pal = Assert.Single(NewReader().Read(file, "s"));
        Assert.Equal(["CraftSpeed_up2"], pal.Passives);
        Assert.Equal(["AirBlade", "AirCanon"], pal.EquippedAttacks);
        Assert.Contains(_log, m => m.Contains("NoSuchPal"));
        Assert.Contains(_log, m => m.Contains("NoSuchPassive"));
        Assert.Contains(_log, m => m.Contains("NoSuchAttack"));
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, \"pals\": [ ")] // half written
    [InlineData("not json")]
    [InlineData("{ \"schemaVersion\": 9, \"pals\": [] }")]
    public void BrokenFile_Throws_SoThePreviousDataStays(string json)
    {
        Assert.Throws<InvalidDataException>(() => NewReader().Read(Write(json), "s"));
    }

    [Fact]
    public void MissingFile_Throws_NotAnEmptyList()
    {
        Assert.Throws<FileNotFoundException>(() => NewReader().Read(Path.Combine(_dir, "nope.json"), "s"));
    }

    [Fact]
    public void Summary_CountsPagesPerKind()
    {
        var file = Write("""
            { "schemaVersion": 1, "exportedAtUtc": "2026-11-12T17:42:00Z", "pals": [
              { "characterId": "CowPal", "location": { "kind": "Palbox", "page": 0, "slot": 0 } },
              { "characterId": "CowPal", "location": { "kind": "Palbox", "page": 1, "slot": 0 } },
              { "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 0, "slot": 0 } },
              { "characterId": "CowPal", "location": { "kind": "Party", "page": 0, "slot": 0 } } ] }
            """);
        var summary = ModBridgeReader.Summarize(file);
        Assert.NotNull(summary);
        Assert.Equal(4, summary.PalCount);
        Assert.Equal(4, summary.ImportedCount);
        Assert.Equal(1, summary.PartyCount);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T17:42:00Z"), summary.ExportedAtUtc);
        Assert.Null(ModBridgeReader.Summarize(Path.Combine(_dir, "nope.json")));
    }

    [Fact]
    public void OwnerUid_OfAPal_OverridesTheFilesPlayer_AndTheFilesPlayerKeepsItsName()
    {
        var file = Write("""
            { "schemaVersion": 1, "playerName": "Me", "playerUid": "AAAA", "pals": [
              { "characterId": "CowPal", "ownerUid": "BBBB", "location": { "kind": "DimensionalStorage", "page": 3, "slot": 7 } },
              { "characterId": "CowPal", "ownerUid": "AAAA", "location": { "kind": "DimensionalStorage", "page": 3, "slot": 8 } } ] }
            """);
        var pals = NewReader().Read(file, "s");
        Assert.Equal("BBBB", pals[0].Owner!.PlayerId);
        Assert.Null(pals[0].Owner!.PlayerName);
        Assert.Equal("AAAA", pals[1].Owner!.PlayerId);
        Assert.Equal("Me", pals[1].Owner!.PlayerName);
        Assert.Null(pals[0].Location.ContainerId); // the page is not a container
        Assert.Equal(3, pals[0].Location.Page);
        Assert.Equal(7, pals[0].Location.SlotIndex);
    }
    [Fact]
    public void BoxOwner_IsPartOfTheContainerAndTheUid_AndCoverageListsTheGaps()
    {
        var file = Write("""
            { "schemaVersion": 1, "pals": [
              { "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 2, "slot": 5, "boxOwnerUid": "0001" } },
              { "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 2, "slot": 5, "boxOwnerUid": "0002" } } ],
              "coverage": [ { "kind": "DimensionalStorage", "boxOwnerUid": "0001", "pageCount": 16, "pagesRead": [0, 1, 3, 6], "lastReadUtc": "2026-11-12T19:00:00Z" } ] }
            """);
        var pals = NewReader().Read(file, "s");
        Assert.Equal(["mod:DimensionalStorage:0001:2:5", "mod:DimensionalStorage:0002:2:5"], pals.Select(p => p.Uid));
        Assert.Equal("0001", pals[0].Location.ContainerId);
        Assert.Equal(2, pals[0].Location.Page);

        var coverage = Assert.Single(ModBridgeReader.Summarize(file)!.Coverage);
        Assert.Equal(16, coverage.PageCount);
        Assert.Equal([2, 4, 5], coverage.MissingPages); // gaps below the highest page read; nothing above it is listed
        Assert.Empty(new BridgeCoverage().MissingPages);
    }
    [Fact]
    public void HumansAndUnknownSpecies_AreNotImported_AndTheSummaryListsThem()
    {
        var file = Write("""
            { "schemaVersion": 1, "pals": [
              { "characterId": "CowPal", "location": { "kind": "Palbox", "page": 0, "slot": 0 } },
              { "characterId": "Hunter_Bat", "location": { "kind": "DimensionalStorage", "page": 1, "slot": 3 } },
              { "characterId": "NoSuchThing", "location": { "kind": "Palbox", "page": 0, "slot": 1 } } ],
              "coverage": [ { "kind": "Palbox", "pagesRead": [0, 1] }, { "kind": "DimensionalStorage", "boxOwnerUid": "0001", "pagesRead": [0, 1, 2] },
                            { "kind": "DimensionalStorage", "boxOwnerUid": "0002", "pagesRead": [4] } ] }
            """);
        Assert.Single(NewReader().Read(file, "s"));
        Assert.Contains(_log, m => m.Contains("human") && m.Contains("Hunter_Bat"));

        var summary = ModBridgeReader.Summarize(file, TestSupport.GameData)!;
        Assert.Equal(3, summary.PalCount);
        Assert.Equal(1, summary.ImportedCount);
        Assert.Equal([new BridgeSkipped("Hunter_Bat", true), new BridgeSkipped("NoSuchThing", false)], summary.Skipped);
        // One definition of "pages read": the pages in the coverage report, empty ones included, summed over the boxes of a kind.
        Assert.Equal(2, summary.PagesRead("Palbox"));
        Assert.Equal(4, summary.PagesRead("DimensionalStorage"));
        Assert.Empty(ModBridgeReader.Summarize(file)!.Skipped); // without game data nothing can be judged
    }

    [Theory]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("")]
    [InlineData(null)]
    public void AnAllZeroBoxOwner_IsNoOwner(string? uid)
    {
        Assert.Null(ModBridgeReader.NormalizeOwner(uid));
        var json = $$"""{ "schemaVersion": 1, "pals": [ { "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 2, "slot": 5, "boxOwnerUid": {{(uid == null ? "null" : "\"" + uid + "\"")}} } } ] }""";
        var pal = Assert.Single(NewReader().Read(Write(json), "s"));
        Assert.Null(pal.Location.ContainerId);
        Assert.Equal(2, pal.Location.Page);
        Assert.Equal("mod:DimensionalStorage:2:5", pal.Uid);
    }
    [Fact]
    public void BoxesWithAnUnknownOwner_ArePagesOfDifferentBoxes_AndTheNameIsPartOfTheContainer()
    {
        var file = Write("""
            { "schemaVersion": 1, "pals": [
              { "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 0, "slot": 0, "boxId": "unknown-1" } },
              { "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 0, "slot": 0, "boxId": "unknown-2" } },
              { "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 0, "slot": 1, "boxId": "name:Alice" } } ],
              "coverage": [ { "kind": "DimensionalStorage", "boxId": "name:Alice", "ownerName": "Alice", "ownerSource": "title", "pagesRead": [0] } ] }
            """);
        var pals = NewReader().Read(file, "s");
        Assert.Equal(["unknown-1", "unknown-2", "name:Alice"], pals.Select(p => p.Location.ContainerId));
        Assert.All(pals, p => Assert.Equal(0, p.Location.Page));
        Assert.Equal(3, pals.Select(p => p.Uid).Distinct().Count());
        var coverage = Assert.Single(ModBridgeReader.Summarize(file)!.Coverage);
        Assert.Equal("Alice", coverage.OwnerName);
        Assert.Equal("title", coverage.OwnerSource);
    }

    [Fact]
    public void PartyPals_AreReadWithTheirSlot()
    {
        var file = Write("""{ "schemaVersion": 1, "pals": [ { "characterId": "CowPal", "location": { "kind": "Party", "page": 0, "slot": 2 } } ] }""");
        var pal = Assert.Single(NewReader().Read(file, "s"));
        Assert.Equal(Paldium.Core.Domain.LocationKind.Party, pal.Location.Kind);
        Assert.Equal(2, pal.Location.SlotIndex);
        Assert.Equal("mod:Party:0:2", pal.Uid);
    }
    [Fact]
    public void BasePals_KeepTheNameOfTheirBase()
    {
        var file = Write("""{ "schemaVersion": 1, "pals": [ { "characterId": "CowPal", "location": { "kind": "Base", "page": 0, "slot": 1, "baseId": "B1", "baseName": "Home" } } ] }""");
        Assert.Equal("Home", Assert.Single(NewReader().Read(file, "s")).Location.BaseName);
    }

    [Theory]
    [InlineData("\u65b0\u898f\u751f\u6210\u62e0\u70b9\u30c6\u30f3\u30d7\u30ec\u30fc\u30c8\u540d1(\u4eee)")]
    [InlineData("Base (\u4eee)")]
    [InlineData("\u30c6\u30f3\u30d7\u30ec\u30fc\u30c8")]
    public void BasePals_DoNotShowTheGamesTemplateBaseName(string placeholder)
    {
        Assert.True(ModBridgeReader.IsPlaceholderBaseName(placeholder));
        var json = "{ \"schemaVersion\": 1, \"pals\": [ { \"characterId\": \"CowPal\", \"location\": { \"kind\": \"Base\", \"page\": 0, \"slot\": 1, \"baseId\": \"B1\", \"baseName\": \"" + placeholder + "\" } } ] }";
        Assert.Null(Assert.Single(NewReader().Read(Write(json), "s")).Location.BaseName);
        Assert.False(ModBridgeReader.IsPlaceholderBaseName("Home"));
        Assert.False(ModBridgeReader.IsPlaceholderBaseName(null));
    }

    [Fact]
    public void BasePals_KeepTheirBaseId()
    {
        var file = Write("""{ "schemaVersion": 1, "pals": [ { "characterId": "CowPal", "location": { "kind": "Base", "page": 0, "slot": 3, "baseId": "0123456789ABCDEF0123456789ABCDEF" } } ] }""");
        var pal = Assert.Single(NewReader().Read(file, "s"));
        Assert.Equal(Paldium.Core.Domain.LocationKind.Base, pal.Location.Kind);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF", pal.Location.BaseId);
        Assert.Equal(3, pal.Location.SlotIndex);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF", pal.Location.ContainerId);
        Assert.Null(pal.Location.Page); // a base has no pages
    }

    [Fact]
    public void ReadAll_AllowsDeleteSharingWhileFileIsOpen()
    {
        var path = Write("""{ "schemaVersion": 1, "playerUid": "AAAA", "pals": [ { "characterId": "CowPal" } ] }""");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var (pals, captures) = NewReader().ReadAll(path, "s");
        Assert.Single(pals);
        Assert.Empty(captures);
    }
}
