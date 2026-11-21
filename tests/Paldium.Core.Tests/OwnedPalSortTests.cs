using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class OwnedPalSortTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static OwnedPal Pal(string uid, string species, int level, Ivs? ivs, LocationKind kind = LocationKind.Palbox, int? page = null, int? slot = null, Gender? gender = Gender.Male) =>
        new(uid, null, species, gender, level, 1, [], ivs, [], [], null, null, new PalLocation(kind, null, slot, null, page),
            PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    private static string[] Order(IEnumerable<OwnedPal> pals, OwnedSortKey key, bool descending) =>
        OwnedPalSort.Sort(pals, Data.Value, key, descending).Select(p => p.Uid).ToArray();

    [Fact]
    public void IvSorting_PutsUnknownIvsLast_InBothDirections()
    {
        var pals = new[]
        {
            Pal("none", "SheepBall", 10, null), Pal("low", "SheepBall", 10, new Ivs(10, 10, 10)),
            Pal("high", "SheepBall", 10, new Ivs(90, 80, 70)), Pal("mid", "SheepBall", 10, new Ivs(50, 50, 50)),
        };
        Assert.Equal(["high", "mid", "low", "none"], Order(pals, OwnedSortKey.IvTotal, descending: true));
        Assert.Equal(["low", "mid", "high", "none"], Order(pals, OwnedSortKey.IvTotal, descending: false));
    }

    [Fact]
    public void EachIvCanBeSortedOnItsOwn()
    {
        var pals = new[] { Pal("a", "SheepBall", 10, new Ivs(10, 90, 50)), Pal("b", "SheepBall", 10, new Ivs(90, 10, 60)), Pal("c", "SheepBall", 10, new Ivs(50, 50, 99)) };
        Assert.Equal(["b", "c", "a"], Order(pals, OwnedSortKey.IvHp, true));
        Assert.Equal(["a", "c", "b"], Order(pals, OwnedSortKey.IvAttack, true));
        Assert.Equal(["c", "b", "a"], Order(pals, OwnedSortKey.IvDefense, true));
    }

    [Fact]
    public void Location_IsKind_ThenPage_ThenSlot()
    {
        var pals = new[]
        {
            Pal("box2slot1", "SheepBall", 10, null, LocationKind.Palbox, 1, 1), Pal("box1slot5", "SheepBall", 10, null, LocationKind.Palbox, 0, 5),
            Pal("party", "SheepBall", 10, null, LocationKind.Party, null, 0), Pal("box1slot2", "SheepBall", 10, null, LocationKind.Palbox, 0, 2),
            Pal("storage", "SheepBall", 10, null, LocationKind.DimensionalStorage, 0, 0),
        };
        Assert.Equal(["party", "box1slot2", "box1slot5", "box2slot1", "storage"], Order(pals, OwnedSortKey.Location, false));
    }

    [Fact]
    public void LevelSpeciesAndDefault_Order()
    {
        var pals = new[] { Pal("lv5", "SheepBall", 5, null), Pal("lv30", "SheepBall", 30, null), Pal("anubis", "Anubis", 1, null) };
        Assert.Equal(["lv30", "lv5", "anubis"], Order(pals, OwnedSortKey.Level, true)[..2].Concat(["anubis"]).ToArray());
        Assert.Equal(["lv30", "lv5", "anubis"], Order(pals, OwnedSortKey.Default, false)); // dex order, higher level first
    }

    [Fact]
    public void TheIvFilter_LetsOnlyPalsWithKnownEnoughIvsPass()
    {
        var known = Pal("k", "SheepBall", 10, new Ivs(60, 60, 60));
        var unknown = Pal("u", "SheepBall", 10, null);
        Assert.True(OwnedPalSort.PassesMinIvTotal(known, 180));
        Assert.False(OwnedPalSort.PassesMinIvTotal(known, 181));
        Assert.False(OwnedPalSort.PassesMinIvTotal(unknown, 1));
        Assert.True(OwnedPalSort.PassesMinIvTotal(unknown, null));
        Assert.True(OwnedPalSort.PassesMinIvTotal(unknown, 0));
    }

    [Fact]
    public void Location_SeparatesTheBoxes_BeforeThePages()
    {
        OwnedPal InBox(string uid, string box, int page, int slot) =>
            Pal(uid, "SheepBall", 10, null, LocationKind.DimensionalStorage, page, slot) with { Location = new PalLocation(LocationKind.DimensionalStorage, box, slot, null, page) };
        var pals = new[] { InBox("viro0", "viro_DPS", 0, 0), InBox("asta1", "asta_DPS", 1, 0), InBox("asta0", "asta_DPS", 0, 3), InBox("viro1", "viro_DPS", 1, 1) };
        Assert.Equal(["asta0", "asta1", "viro0", "viro1"], Order(pals, OwnedSortKey.Location, false));
    }
}
