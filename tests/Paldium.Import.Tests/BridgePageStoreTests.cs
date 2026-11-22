using Paldium.Import.Bridge;

namespace Paldium.Import.Tests;

public class BridgePageStoreTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempFolder();
    private readonly List<string> _log = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private BridgePageStore NewStore() => new(_dir, (m, _) => _log.Add(m));

    private static BridgePal Pal(string instanceId, string kind = "DimensionalStorage", int page = 0, int slot = 0, string? boxId = "0001", string? baseId = null) =>
        new() { CharacterId = "CowPal", InstanceId = instanceId, Location = new BridgeLocation(kind, page, slot, null, boxId, baseId) };

    private static BridgeCoverage Read(string kind, string? boxId, params int[] pages) =>
        new() { Kind = kind, BoxId = boxId, PagesRead = pages, LastReadUtc = DateTimeOffset.Parse("2026-11-12T20:00:00Z"), PageCount = 16 };

    private static BridgeFile File(string? world, IReadOnlyList<BridgePal> pals, params BridgeCoverage[] coverage) =>
        new() { SchemaVersion = 1, PlayerUid = "PLAYER", WorldId = world, ExportedAtUtc = DateTimeOffset.Parse("2026-11-12T20:00:00Z"), Pals = pals, Coverage = coverage };

    private static IEnumerable<string?> Ids(BridgeMergeResult result) => result.Pals.Select(p => p.InstanceId).Order();

    [Fact]
    public void APageThatWasNotReadAgain_IsKept_AndAPageThatWasRead_IsReplaced()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Pal("A1", page: 0), Pal("B1", page: 1)], Read("DimensionalStorage", "0001", 0, 1)));

        // The next session reads only page 0 (and a Pal moved in).
        var merged = store.Merge("s", File("w", [Pal("A1", page: 0), Pal("A2", page: 0, slot: 1)], Read("DimensionalStorage", "0001", 0)));

        Assert.Equal(["A1", "A2", "B1"], Ids(merged)); // page 1 survived
    }

    [Fact]
    public void AnEmptyPageThatWasRead_RemovesItsOldContent()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Pal("A1", "Palbox", page: 0, boxId: null)], Read("Palbox", null, 0)));

        var merged = store.Merge("s", File("w", [], Read("Palbox", null, 0)));

        Assert.Empty(merged.Pals);
        Assert.Equal([0], Assert.Single(merged.Coverage).PagesRead); // read, and empty
    }

    [Fact]
    public void APalOnTwoPages_IsKeptOnce_TheNewestReadWins()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Pal("A1", page: 2, slot: 4)], Read("DimensionalStorage", "0001", 2)));

        // It was moved to page 0 and page 0 is read later; page 2 (older) still lists it.
        var later = new BridgeFile
        {
            SchemaVersion = 1, WorldId = "w", PlayerUid = "PLAYER", ExportedAtUtc = DateTimeOffset.Parse("2026-11-13T09:00:00Z"),
            Pals = [Pal("A1", page: 0, slot: 1)],
            Coverage = [new BridgeCoverage { Kind = "DimensionalStorage", BoxId = "0001", PagesRead = [0], LastReadUtc = DateTimeOffset.Parse("2026-11-13T09:00:00Z") }],
        };
        var merged = store.Merge("s", later);

        var pal = Assert.Single(merged.Pals);
        Assert.Equal(0, pal.Location!.Page);
        Assert.Equal(1, pal.Location.Slot);
    }

    [Fact]
    public void TheWorldsAreKeptApart()
    {
        var store = NewStore();
        store.Merge("s", File("world1", [Pal("A1")], Read("DimensionalStorage", "0001", 0)));

        var other = store.Merge("s", File("world2", [Pal("B1")], Read("DimensionalStorage", "0001", 0)));
        Assert.Equal(["B1"], Ids(other)); // nothing of world1

        var back = store.Merge("s", File("world1", [Pal("A1")], Read("DimensionalStorage", "0001", 0)));
        Assert.Equal(["A1"], Ids(back)); // and world1 is still there when the player comes back
    }

    [Fact]
    public void WithoutAWorldId_ThePlayerUidIsTheWorld()
    {
        var store = NewStore();
        store.Merge("s", File(null, [Pal("A1")], Read("DimensionalStorage", "0001", 0)));
        Assert.Equal("PLAYER", BridgePageStore.WorldOf(File(null, [])));
        Assert.Equal(["A1"], Ids(store.Merge("s", File(null, [], Read("DimensionalStorage", "0001", 1)))));
    }

    [Fact]
    public void TheParty_IsReplacedAsAWhole_OtherBoxesAreSeparate()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Pal("P1", "Party", 0, 0, null), Pal("P2", "Party", 0, 1, null), Pal("A1")],
            Read("Party", null, 0), Read("DimensionalStorage", "0001", 0)));

        var merged = store.Merge("s", File("w", [Pal("P3", "Party", 0, 0, null)], Read("Party", null, 0)));

        Assert.Equal(["A1", "P3"], Ids(merged));
        Assert.Equal(2, merged.Coverage.Count);
    }

    [Fact]
    public void TwoBoxesOfTheSameKind_AreTwoCoverageEntries_WithTheOldestRead()
    {
        var store = NewStore();
        var old = new BridgeCoverage { Kind = "DimensionalStorage", BoxId = "0001", PagesRead = [0], LastReadUtc = DateTimeOffset.Parse("2026-11-10T08:00:00Z"), OwnerName = "Alice" };
        var fresh = new BridgeCoverage { Kind = "DimensionalStorage", BoxId = "0002", PagesRead = [0, 1], LastReadUtc = DateTimeOffset.Parse("2026-11-12T08:00:00Z") };
        var merged = store.Merge("s", File("w", [Pal("A1", boxId: "0001"), Pal("B1", boxId: "0002")], old, fresh));

        Assert.Equal(2, merged.Coverage.Count);
        var first = merged.Coverage.Single(c => c.BoxId == "0001");
        Assert.Equal("Alice", first.OwnerName);
        Assert.Equal(DateTimeOffset.Parse("2026-11-10T08:00:00Z"), first.OldestReadUtc);
        Assert.Equal([0, 1], merged.Coverage.Single(c => c.BoxId == "0002").PagesRead);
    }

    [Fact]
    public void ThePagesSurviveANewStoreInstance_AndClearForgetsThem()
    {
        NewStore().Merge("s", File("w", [Pal("A1")], Read("DimensionalStorage", "0001", 0)));

        var again = NewStore(); // a new process
        Assert.Equal(["A1"], Ids(again.Merge("s", File("w", [], Read("DimensionalStorage", "0001", 1)))));
        Assert.Single(again.Coverage("s", "w"), c => c.PagesRead.Contains(0));

        again.Clear("s");
        Assert.Empty(again.Coverage("s", "w"));
        Assert.Empty(again.Merge("s", File("w", [])).Pals);
    }

    [Fact]
    public void ABrokenStoreFile_StartsEmpty_WithAWarning_AndIsRepaired()
    {
        System.IO.File.WriteAllText(Path.Combine(_dir, "s.json"), "{ not json");
        var store = NewStore();

        var merged = store.Merge("s", File("w", [Pal("A1")], Read("DimensionalStorage", "0001", 0)));

        Assert.Equal(["A1"], Ids(merged));
        Assert.Contains(_log, m => m.Contains("broken"));
        Assert.Equal(["A1"], Ids(NewStore().Merge("s", File("w", []))));
    }

    [Fact]
    public void Reader_WithAStore_KeepsThePagesOfEarlierSessions()
    {
        var file = Path.Combine(_dir, "palbox.json");
        var reader = new ModBridgeReader(TestSupport.GameData, null, NewStore());

        System.IO.File.WriteAllText(file, """
            { "schemaVersion": 1, "worldId": "w", "pals": [
              { "instanceId": "AAA", "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 0, "slot": 0, "boxId": "0001" } },
              { "instanceId": "BBB", "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 1, "slot": 0, "boxId": "0001" } } ],
              "coverage": [ { "kind": "DimensionalStorage", "boxId": "0001", "pagesRead": [0, 1] } ] }
            """);
        Assert.Equal(2, reader.Read(file, "s").Count);

        // After a restart the mod has read one page only.
        System.IO.File.WriteAllText(file, """
            { "schemaVersion": 1, "worldId": "w", "pals": [
              { "instanceId": "AAA", "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 0, "slot": 0, "boxId": "0001" } } ],
              "coverage": [ { "kind": "DimensionalStorage", "boxId": "0001", "pagesRead": [0] } ] }
            """);
        var pals = reader.Read(file, "s");
        Assert.Equal(2, pals.Count); // not 1

        // Without a store the file is read as it is.
        Assert.Single(new ModBridgeReader(TestSupport.GameData).Read(file, "s"));
    }

    [Fact]
    public void EveryPal_IsAsOldAsThePageItWasReadFrom()
    {
        var file = Path.Combine(_dir, "palbox.json");
        var reader = new ModBridgeReader(TestSupport.GameData, null, NewStore());

        System.IO.File.WriteAllText(file, """
            { "schemaVersion": 1, "exportedAtUtc": "2026-11-10T20:00:00Z", "worldId": "w", "pals": [
              { "instanceId": "AAA", "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 0, "slot": 0, "boxId": "0001" } } ],
              "coverage": [ { "kind": "DimensionalStorage", "boxId": "0001", "pagesRead": [0], "lastReadUtc": "2026-11-10T20:00:00Z" } ] }
            """);
        reader.Read(file, "s");

        // Three days later only page 1 is read; page 0 (old) must keep its old time.
        System.IO.File.WriteAllText(file, """
            { "schemaVersion": 1, "exportedAtUtc": "2026-11-13T20:00:00Z", "worldId": "w", "pals": [
              { "instanceId": "BBB", "characterId": "CowPal", "location": { "kind": "DimensionalStorage", "page": 1, "slot": 0, "boxId": "0001" } } ],
              "coverage": [ { "kind": "DimensionalStorage", "boxId": "0001", "pagesRead": [1], "lastReadUtc": "2026-11-13T20:00:00Z" } ] }
            """);
        var pals = reader.Read(file, "s");

        Assert.Equal(DateTimeOffset.Parse("2026-11-10T20:00:00Z"), pals.Single(p => p.InstanceId == "AAA").SeenAtUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-11-13T20:00:00Z"), pals.Single(p => p.InstanceId == "BBB").SeenAtUtc);
    }

    [Fact]
    public void UnknownBoxesOfDifferentSessions_DoNotOverwriteEachOther()
    {
        var store = NewStore();
        // Yesterday's session: the friend's storage, owner unknown, 3 pages.
        store.Merge("s", File("w", [Pal("F1", page: 0, boxId: "unknown-20261112203000-1"), Pal("F2", page: 1, boxId: "unknown-20261112203000-1"), Pal("F3", page: 2, boxId: "unknown-20261112203000-1")],
            Read("DimensionalStorage", "unknown-20261112203000-1", 0, 1, 2)));

        // Today's session also calls its first unknown box number 1: the player's own storage, pages 0-1 empty.
        var merged = store.Merge("s", File("w", [], Read("DimensionalStorage", "unknown-20261113090000-1", 0, 1)));

        Assert.Equal(["F1", "F2", "F3"], Ids(merged)); // nothing was lost
        // The empty storage nobody owns is dropped (T60); the friend's storage stays under its own key.
        Assert.Equal("unknown-20261112203000-1", Assert.Single(merged.Coverage, c => c.Kind == "DimensionalStorage").BoxId);
    }

    private static BridgePal Stored(string instanceId, string box, int page = 0, int slot = 0) => Pal(instanceId, "DimensionalStorage", page, slot, box);

    private static BridgeCoverage Owned(string box, string? uid, string? name, string? source, params int[] pages) =>
        Read("DimensionalStorage", box, pages) with { BoxOwnerUid = uid, OwnerName = name, OwnerSource = source };

    private const string Alice = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Bob = "00000000000000000000000000000001";

    [Fact]
    public void AnIncomingStorage_WithSharedPals_IsTheSameStorageAsTheStoredOne_AndKeepsItsOwner()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Stored("A1", Alice), Stored("A2", Alice, slot: 1)], Owned(Alice, Alice, "Alice", "structure", 0)));

        // A new session: the owner is not found, the key is an unknown one, but A1 is in it.
        var merged = store.Merge("s", File("w", [Stored("A1", "unknown-20261114000000-1"), Stored("A3", "unknown-20261114000000-1", slot: 2)],
            Read("DimensionalStorage", "unknown-20261114000000-1", 0)));

        var coverage = Assert.Single(merged.Coverage, c => c.Kind == "DimensionalStorage");
        Assert.Equal(Alice, coverage.BoxId);
        Assert.Equal("Alice", coverage.OwnerName);
        Assert.Equal(["A1", "A3"], Ids(merged).Order().ToList()); // page 0 was read again: A2 left the page
        Assert.All(merged.Pals, p => Assert.Equal(Alice, p.Location!.BoxId));
    }

    [Fact]
    public void AnIncomingStorage_WithKnownOwner_TakesOverAnUnknownStoredOne()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Stored("A1", "unknown-1", page: 0), Stored("A2", "unknown-1", page: 1)], Read("DimensionalStorage", "unknown-1", 0, 1)));

        var merged = store.Merge("s", File("w", [Stored("A1", Alice, page: 0)], Owned(Alice, Alice, "Alice", "structure", 0)));

        var coverage = Assert.Single(merged.Coverage, c => c.Kind == "DimensionalStorage");
        Assert.Equal(Alice, coverage.BoxId);
        Assert.Equal([0, 1], coverage.PagesRead); // the unread page of the old box moved along
        Assert.Equal(["A1", "A2"], Ids(merged));
    }

    [Fact]
    public void AnIncomingStorage_WhosePagesMatchSeveralStoredOnes_MergesThem()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Stored("B1", "unknown-1"), Stored("B2", "unknown-1", slot: 1)], Read("DimensionalStorage", "unknown-1", 0)));
        store.Merge("s", File("w", [Stored("C1", "unknown-2", page: 1), Stored("C2", "unknown-2", page: 1, slot: 1)], Read("DimensionalStorage", "unknown-2", 1)));

        var merged = store.Merge("s", File("w", [Stored("B1", "unknown-3"), Stored("B2", "unknown-3", slot: 1), Stored("C1", "unknown-3", page: 1), Stored("C2", "unknown-3", page: 1, slot: 1)],
            Read("DimensionalStorage", "unknown-3", 0, 1)));

        var coverage = Assert.Single(merged.Coverage, c => c.Kind == "DimensionalStorage");
        Assert.Equal([0, 1], coverage.PagesRead);
        Assert.Equal(["B1", "B2", "C1", "C2"], Ids(merged));
    }

    [Fact]
    public void OneSharedPal_DoesNotMakeTwoStoragesTheSame()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Stored("A1", "unknown-1"), Stored("A2", "unknown-1", slot: 1), Stored("A3", "unknown-1", slot: 2)], Read("DimensionalStorage", "unknown-1", 0)));

        // A friend's storage that holds A1 (moved) and its own Pals.
        var merged = store.Merge("s", File("w", [Stored("A1", "unknown-2"), Stored("F1", "unknown-2", slot: 1), Stored("F2", "unknown-2", slot: 2)], Read("DimensionalStorage", "unknown-2", 0)));

        Assert.Equal(2, merged.Coverage.Count(c => c.Kind == "DimensionalStorage"));
        Assert.Equal(["A1", "A2", "A3", "F1", "F2"], Ids(merged));
    }

    [Fact]
    public void EightPalsMovedFromAFullPageToAnEmptyStorage_KeepTheStoragesApart_AndNoPalIsLost()
    {
        var store = NewStore();
        var a = Enumerable.Range(0, 29).Select(i => Stored("A" + i, Alice, slot: i)).ToList();
        store.Merge("s", File("w", a, Owned(Alice, Alice, "Alice", "structure", 0)));

        // 8 of them were moved to the friend's (before empty) storage; both storages are read afterwards.
        var friend = Enumerable.Range(0, 8).Select(i => Stored("A" + i, "unknown-1", slot: i)).ToList();
        var merged = store.Merge("s", File("w", [.. a.Skip(8), .. friend], Owned(Alice, Alice, "Alice", "structure", 0), Read("DimensionalStorage", "unknown-1", 0)));

        Assert.Equal(2, merged.Coverage.Count(c => c.Kind == "DimensionalStorage"));
        Assert.Equal(29, merged.Pals.Count);
        Assert.Equal(21, Assert.Single(merged.Coverage, c => c.BoxId == Alice).PalCount);
        Assert.Equal(8, Assert.Single(merged.Coverage, c => c.BoxId == "unknown-1").PalCount);
    }

    [Fact]
    public void TwoStoragesOfDifferentKnownOwners_AreNeverMerged_EvenWhenAPalMovedBetweenThem()
    {
        var store = NewStore();
        var alice = new[] { Stored("A1", Alice), Stored("A2", Alice, slot: 1), Stored("A3", Alice, slot: 2) };
        var bob = new[] { Stored("V1", Bob), Stored("V2", Bob, slot: 1), Stored("V3", Bob, slot: 2) };
        store.Merge("s", File("w", [.. alice, .. bob], Owned(Alice, Alice, "Alice", "structure", 0), Owned(Bob, Bob, "bob", "structure", 0)));

        // A1 was moved to bob's storage, whose page is read again.
        var merged = store.Merge("s", File("w", [Stored("V1", Bob), Stored("V2", Bob, slot: 1), Stored("A1", Bob, slot: 2)], Owned(Bob, Bob, "bob", "structure", 0)));

        Assert.Equal(2, merged.Coverage.Count(c => c.Kind == "DimensionalStorage"));
        Assert.Equal(["A1", "A2", "A3", "V1", "V2"], Ids(merged)); // V3 left the storage; nothing else was lost
    }

    [Fact]
    public void ASortedStorage_ReadUnderANewKey_AbsorbsTheOldOne()
    {
        var store = NewStore();
        var pals = Enumerable.Range(0, 20).Select(i => Stored("S" + i, Alice, page: i / 10, slot: i % 10)).ToList();
        store.Merge("s", File("w", pals, Owned(Alice, Alice, "Alice", "structure", 0, 1)));

        // The storage was sorted: the same Pals are on other pages now and are read under an unknown key.
        var sorted = Enumerable.Range(0, 20).Select(i => Stored("S" + (19 - i), "unknown-9", page: i / 10, slot: i % 10)).ToList();
        var merged = store.Merge("s", File("w", sorted, Read("DimensionalStorage", "unknown-9", 0, 1)));

        var box = Assert.Single(merged.Coverage, c => c.Kind == "DimensionalStorage");
        Assert.Equal(Alice, box.BoxId); // the known owner stays
        Assert.Equal(20, merged.Pals.Count);
    }

    [Fact]
    public void AnEmptyStorage_IsDeleted_EvenWhenItsOwnerIsKnown()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Stored("A1", Alice)], Owned(Alice, Alice, "Alice", "structure", 0)));

        var merged = store.Merge("s", File("w", [], Owned(Bob, Bob, "bob", "structure", 0, 1)));

        var box = Assert.Single(merged.Coverage, c => c.Kind == "DimensionalStorage");
        Assert.Equal(Alice, box.BoxId); // bob's empty box is gone
    }

    [Theory]
    [InlineData(8, 29, 8, false)]
    [InlineData(8, 8, 8, true)]
    [InlineData(5, 10, 5, true)]
    [InlineData(5, 10, 4, false)]
    [InlineData(1, 30, 1, false)]
    public void SamePage_NeedsAtLeastHalfOfEachPage(int a, int b, int matched, bool expected)
    {
        var x = Enumerable.Range(0, a).Select(i => "P" + i).ToList();
        var y = Enumerable.Range(0, b).Select(i => i < matched ? "P" + i : "Q" + i).ToList();
        Assert.Equal(expected, BridgePageStore.SamePage(x, y));
    }

    [Fact]
    public void ANinthTestLikeStore_IsTidiedUpOnTheFirstMerge()
    {
        var store = NewStore();
        // Alice's old box, three unknown boxes that hold the same Pals (in other sessions), bob's 13 Pals in an unknown box and an empty bob box.
        var alice = Enumerable.Range(0, 20).Select(i => Stored("A" + i, Alice, page: i / 10, slot: i % 10)).ToList();
        store.Merge("s", File("w", alice, Owned(Alice, Alice, "Alice", "structure", 0, 1)));
        store.Merge("s", File("w", alice.Take(10).Select(p => p with { Location = p.Location! with { BoxId = "unknown-1" } }).ToList(), Read("DimensionalStorage", "unknown-1", 0)));
        store.Merge("s", File("w", alice.Skip(10).Select(p => p with { Location = p.Location! with { BoxId = "unknown-2" } }).Concat([Stored("A0", "unknown-2", page: 5)]).ToList(), Read("DimensionalStorage", "unknown-2", 1, 5)));
        var viroPals = Enumerable.Range(0, 13).Select(i => Stored("V" + i, "unknown-3", slot: i)).ToList();
        store.Merge("s", File("w", viroPals, Read("DimensionalStorage", "unknown-3", 0)));
        var merged = store.Merge("s", File("w", [], Read("DimensionalStorage", "unknown-4", 0)));

        var boxes = merged.Coverage.Where(c => c.Kind == "DimensionalStorage").ToList();
        Assert.Equal(2, boxes.Count);
        Assert.Equal(Alice, Assert.Single(boxes, b => b.OwnerName == "Alice").BoxId);
        Assert.Equal(13, Assert.Single(boxes, b => b.BoxId == "unknown-3").PalCount);
        Assert.Equal(33, merged.Pals.Count); // 20 + 13: the number did not change
    }

    [Fact]
    public void TheOwnerTheUserChose_IsKept_ForTheNextSession_AndTheGamesAnswerWins()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Stored("V1", "unknown-1"), Stored("V2", "unknown-1", slot: 1)], Read("DimensionalStorage", "unknown-1", 0)) with { Players = [new BridgePlayer(Bob, "bob")] });
        Assert.Equal("bob", Assert.Single(store.Players("s")).Name);

        var chosen = store.SetBoxOwner("s", "w", "unknown-1", Bob, "bob");
        var box = Assert.Single(chosen.Coverage, c => c.Kind == "DimensionalStorage");
        Assert.Equal("bob", box.OwnerName);
        Assert.Equal(BridgePageStore.UserOwnerSource, box.OwnerSource);

        // The next session finds the storage again through its Pals, under another unknown key: the choice stays.
        var next = store.Merge("s", File("w", [Stored("V1", "unknown-20261114-1"), Stored("V2", "unknown-20261114-1", slot: 1)], Read("DimensionalStorage", "unknown-20261114-1", 0)));
        var again = Assert.Single(next.Coverage, c => c.Kind == "DimensionalStorage");
        Assert.Equal("bob", again.OwnerName);
        Assert.Equal(2, next.Pals.Count);

        // The game finally reads the owner: the game's name replaces the user's choice.
        var game = store.Merge("s", File("w", [Stored("V1", Bob)], Owned(Bob, Bob, "Bob (game)", "structure", 0)));
        var final = Assert.Single(game.Coverage, c => c.Kind == "DimensionalStorage");
        Assert.Equal("Bob (game)", final.OwnerName);
        Assert.Equal("structure", final.OwnerSource);
    }

    [Fact]
    public void TheReader_GivesStoragePalsTheNameOfTheirOwner_AndParsesThePlayersArray()
    {
        var path = Path.Combine(_dir, "palbox.json");
        System.IO.File.WriteAllText(path, """
            { "schemaVersion": 1, "playerUid": "PLAYER", "worldId": "w",
              "players": [ { "uid": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "name": "Alice" } ],
              "pals": [ { "characterId": "CowPal", "instanceId": "A1", "location": { "kind": "DimensionalStorage", "page": 0, "slot": 0, "boxId": "unknown-1" } } ],
              "coverage": [ { "kind": "DimensionalStorage", "boxId": "unknown-1", "pagesRead": [0] } ] }
            """);
        var store = NewStore();
        var reader = new ModBridgeReader(TestSupport.GameData, null, store);
        Assert.Null(Assert.Single(reader.Read(path, "s")).Location.BaseName); // owner unknown

        store.SetBoxOwner("s", "w", "unknown-1", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "Alice");
        Assert.Equal("Alice", Assert.Single(reader.Read(path, "s")).Location.BaseName);
        Assert.Equal("Alice", Assert.Single(store.Players("s")).Name);
    }

    [Fact]
    public void Merge_PreservesKnownBoxMetadata_AndConsolidatePrunesEmptiedBases()
    {
        var store = NewStore();
        store.Merge("s", File("w", [Stored("A1", Alice)], Owned(Alice, Alice, "Alice", "structure", 0)) with
        {
            Coverage = [Owned(Alice, Alice, "Alice", "structure", 0), Read("Base", "B1", 0)],
            Pals = [Stored("A1", Alice), Pal("P1", "Base", page: 0, slot: 0, boxId: null, baseId: "B1")],
        });

        // A partial coverage update knows OwnerName without repeating uid/ownerSource/pageCount: metadata stays intact.
        store.Merge("s", File("w", [Stored("A1", Alice)], new BridgeCoverage { Kind = "DimensionalStorage", BoxId = Alice, OwnerName = "Alice", PagesRead = [0] }));
        var box = Assert.Single(store.Coverage("s", "w"), c => c.Kind == "DimensionalStorage");
        Assert.Equal(Alice, box.BoxOwnerUid);
        Assert.Equal("structure", box.OwnerSource);
        Assert.Equal(16, box.PageCount);

        // Moving the only Pal from base B1 into Palbox later prunes dismantled base B1.
        var later = new BridgeFile
        {
            SchemaVersion = 1, WorldId = "w", PlayerUid = "PLAYER", ExportedAtUtc = DateTimeOffset.Parse("2026-11-13T10:00:00Z"),
            Pals = [Pal("P1", "Palbox", page: 0, slot: 0, boxId: null)],
            Coverage = [Read("Palbox", null, 0)],
        };
        var afterMove = store.Merge("s", later);
        Assert.DoesNotContain(afterMove.Coverage, c => c.Kind == "Base" && c.BoxId == "B1");
    }
}
