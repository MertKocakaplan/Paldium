using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class PalDropTests
{
    private static readonly Lazy<GameData> Real = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static IGameData Data => Real.Value;

    private static List<string> Ids(PalFilter filter, PalSort? sort = null) =>
        PalQuery.Run(Data, filter, sort ?? new PalSort(), "en").Select(p => p.Id).ToList();

    private static PalFilter Seeds(PalDropSource source = PalDropSource.Any) => new() { DropItem = "PotatoSeeds", DropSource = source };

    // Species with made-up drops, so the order rules are tested without depending on the game's numbers.
    private static PalSpecies Fake(string id, int dex, (double Rate, int Min, int Max)? defeat = null, params (int Rank, double Chance, int Min, int Max)[] ranch)
    {
        var names = new LocalizedText(new Dictionary<string, string> { ["en"] = "Item", ["tr"] = "Eşya" }, "en");
        return Data.Pals[0] with
        {
            Id = id,
            DexNo = dex,
            Drops = defeat is { } d ? [new DropInfo("Thing", names, d.Rate, d.Min, d.Max, 0)] : [],
            Ranch = ranch.Select(r => new RanchRank(r.Rank, [new RanchDrop("Thing", names, null, 1, r.Chance, r.Min, r.Max)])).ToList(),
        };
    }

    [Fact]
    public void PotatoSeeds_DropOnDefeatFromThreeSpecies_AndAtTheRanchFromOne()
    {
        Assert.Equal(["FlowerDoll", "RobinHood", "RobinHood_Ground"], Ids(Seeds(PalDropSource.Defeat)).Order(StringComparer.Ordinal));
        Assert.Equal(["VioletFairy"], Ids(Seeds(PalDropSource.Ranch)));
        Assert.Equal(["FlowerDoll", "RobinHood", "RobinHood_Ground", "VioletFairy"], Ids(Seeds()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheItemId_IsMatchedWithoutLetterCase()
    {
        Assert.Equal(Ids(Seeds()), Ids(new PalFilter { DropItem = "potatoseeds" }));
    }

    [Fact]
    public void TheDropFilter_CombinesWithTheOtherFilters_AndMakesTheFilterNotEmpty()
    {
        var work = new Dictionary<string, int> { ["Farming"] = 1 };

        Assert.Equal(["VioletFairy"], Ids(Seeds() with { MinWork = work }));
        Assert.False(new PalFilter { DropItem = "PotatoSeeds" }.IsEmpty);
        Assert.True(new PalFilter { DropSource = PalDropSource.Ranch }.IsEmpty);
    }

    [Fact]
    public void ADrop_CarriesTheDefeatEntry_AndTheFirstRanchRank()
    {
        var pal = Fake("A", 1, (0.5, 1, 2), (7, 0.3, 2, 4), (5, 0.18, 1, 3));

        var both = PalDrops.Of(pal, "thing", PalDropSource.Any);
        Assert.Equal(0.5, both.Defeat!.Rate);
        Assert.Equal((5, 0.18), (both.Ranch!.Rank, both.Ranch.Drop.Chance));
        Assert.Null(PalDrops.Of(pal, "Thing", PalDropSource.Ranch).Defeat);
        Assert.Null(PalDrops.Of(pal, "Thing", PalDropSource.Defeat).Ranch);
        Assert.False(PalDrops.Of(pal, "Other", PalDropSource.Any).Found);
    }

    [Fact]
    public void TheChanceSort_PutsTheHighestChanceFirst_ThenTheLargerAmount_ThenTheDexNumber()
    {
        PalSpecies[] pals =
        [
            Fake("low", 1, (0.1, 5, 5)),
            Fake("highSmall", 2, (0.9, 1, 1)),
            Fake("highBigLate", 4, (0.9, 2, 3)),
            Fake("highBigEarly", 3, (0.9, 2, 3)),
            Fake("ranchBest", 5, (0.05, 1, 1), (1, 0.95, 1, 1)), // with every source the better of the two counts
            Fake("none", 0),
        ];

        var sort = new PalSort { Key = PalSortKey.DropChance, Descending = true };
        var all = new PalFilter { DropItem = "Thing" };

        Assert.Equal(["ranchBest", "highBigEarly", "highBigLate", "highSmall", "low", "none"], PalQuery.Sort(pals, sort, "en", drop: all).Select(p => p.Id));
        Assert.Equal(["highBigEarly", "highBigLate", "highSmall", "low", "ranchBest"],
            PalQuery.Sort(pals, sort, "en", drop: all with { DropSource = PalDropSource.Defeat }).Select(p => p.Id).Where(id => id != "none"));
        Assert.Equal("low", PalQuery.Sort(pals.Take(3), sort with { Descending = false }, "en", drop: all)[0].Id);
    }

    [Fact]
    public void TheChanceSort_WithoutAnItem_FallsBackToTheDexNumber()
    {
        var sorted = Ids(new PalFilter(), new PalSort { Key = PalSortKey.DropChance, Descending = true });

        Assert.Equal(Ids(new PalFilter()), sorted);
    }

    [Fact]
    public void Run_SortsByTheChanceOfTheFilteredItem()
    {
        var sorted = PalQuery.Run(Data, Seeds(), new PalSort { Key = PalSortKey.DropChance, Descending = true }, "en");

        // 100 % with 1-2 seeds before 100 % with 1, the Ranch's 18 % last.
        Assert.Equal(["RobinHood", "RobinHood_Ground", "FlowerDoll", "VioletFairy"], sorted.Select(p => p.Id));
    }

    [Fact]
    public void TheItemList_CountsEverySpeciesOnce_MostDroppedFirst()
    {
        var items = PalDrops.Items(Data.Pals, "en");

        Assert.Equal(4, items.Single(i => i.ItemId == "PotatoSeeds").PalCount);
        Assert.True(items.Zip(items.Skip(1)).All(p => p.First.PalCount >= p.Second.PalCount));
        Assert.Equal(items.Count, items.Select(i => i.ItemId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Ids that differ only in letter case are one item.
        Assert.Single(items, i => string.Equals(i.ItemId, "Poppy", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("patates")]
    [InlineData("PATATES TOHUM")]
    [InlineData("potato se")]
    public void TheItemSearch_ReadsBothLanguages_WhateverTheCase(string query)
    {
        Assert.Contains(PalDrops.Search(PalDrops.Items(Data.Pals, "en"), query), i => i.ItemId == "PotatoSeeds");
    }

    [Fact]
    public void TheItemSearch_IgnoresTurkishLetters()
    {
        var items = PalDrops.Items(Data.Pals, "tr");
        var turkish = items.First(i => i.Names.Get("tr").IndexOfAny(['ı', 'ş', 'ğ', 'ü', 'ö', 'ç', 'İ', 'Ş']) >= 0);
        var plain = TextFold.Fold(turkish.Names.Get("tr"));

        Assert.Contains(PalDrops.Search(items, plain), i => i.ItemId == turkish.ItemId);
    }

    [Fact]
    public void TheSuggestion_NeedsThreeLetters_AndPrefersTheExactName()
    {
        var items = PalDrops.Items(Data.Pals, "en");

        Assert.Equal("PotatoSeeds", PalDrops.Suggest(items, "Potato Seeds")!.ItemId);
        Assert.Equal("Wool", PalDrops.Suggest(items, "wool")!.ItemId);
        Assert.Null(PalDrops.Suggest(items, "po"));
        Assert.Null(PalDrops.Suggest(items, "zzzz"));
        Assert.Null(PalDrops.Suggest(items, null));
    }

    [Fact]
    public void AnItemId_FromALink_IsReadWithoutLetterCase_AndUnknownOnesAreRefused()
    {
        var items = PalDrops.Items(Data.Pals, "en");

        Assert.Equal("PotatoSeeds", PalDrops.Find(items, "potatoSEEDS")!.ItemId);
        Assert.Null(PalDrops.Find(items, "NoSuchItem"));
        Assert.Null(PalDrops.Find(items, ""));
    }
}
