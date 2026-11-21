using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class PalQueryTests
{
    private static readonly Lazy<GameData> Real = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static readonly Lazy<BreedingCalculator> Breeding =
        new(() => BreedingCalculator.Load(Real.Value, TestPaths.DataFolder));

    private static IGameData Data => Real.Value;

    private static List<string> Ids(PalFilter? filter = null, PalSort? sort = null, string lang = "en") =>
        PalQuery.Run(Data, filter ?? new PalFilter(), sort ?? new PalSort(), lang).Select(p => p.Id).ToList();

    // The bundle has no element data until T21, so element tests use a copy with elements filled in.
    private static GameData WithElements(IReadOnlyDictionary<string, string[]> elementsById)
    {
        var d = Real.Value;
        var pals = d.Pals.Select(p => elementsById.TryGetValue(p.Id, out var e) ? p with { Elements = e } : p).ToList();
        return new GameData(d.Meta, d.Mechanics, pals, d.Passives, d.Actives, d.Elements, d.WorkTypes);
    }

    [Fact]
    public void EmptyFilter_ReturnsAllPals_InDexOrder()
    {
        var all = PalQuery.Run(Data, new PalFilter(), new PalSort(), "en");
        Assert.Equal(299, all.Count);
        Assert.True(new PalFilter().IsEmpty);
        for (var i = 1; i < all.Count; i++)
        {
            var prev = all[i - 1];
            var cur = all[i];
            Assert.True(prev.DexNo < cur.DexNo || (prev.DexNo == cur.DexNo && !prev.IsVariant && cur.IsVariant),
                $"{prev.Id} before {cur.Id}");
        }
    }

    [Fact]
    public void Text_FiltersByNameAndNumber_InBothLanguages()
    {
        Assert.Contains("CowPal", Ids(new PalFilter { Text = "mozz" }));
        Assert.Equal(["CowPal"], Ids(new PalFilter { Text = "40" }));
        var turkish = Data.Pal("Sekhmet").Names.Get("tr");
        Assert.Contains("Sekhmet", Ids(new PalFilter { Text = turkish }));
    }

    [Fact]
    public void Work_FiltersByMinimumLevel_AndCombinesWithAnd()
    {
        var handiwork3 = Ids(new PalFilter { MinWork = new Dictionary<string, int> { ["Handiwork"] = 3 } });
        Assert.NotEmpty(handiwork3);
        Assert.All(handiwork3, id => Assert.True(Data.Pal(id).Work["Handiwork"] >= 3));
        Assert.Contains("Sekhmet", handiwork3);

        var both = Ids(new PalFilter
        {
            MinWork = new Dictionary<string, int> { ["Handiwork"] = 3, ["Mining"] = 3 },
        });
        Assert.NotEmpty(both);
        Assert.True(both.Count < handiwork3.Count);
        Assert.All(both, id => Assert.True(Data.Pal(id).Work.GetValueOrDefault("Mining") >= 3));
    }

    [Fact]
    public void Acceptance_Handiwork3_SortedByBreedingPowerAscending()
    {
        var result = PalQuery.Run(
            Data,
            new PalFilter { MinWork = new Dictionary<string, int> { ["Handiwork"] = 3 } },
            new PalSort { Key = PalSortKey.BreedingPower },
            "en");

        Assert.NotEmpty(result);
        Assert.All(result, p => Assert.True(p.Work["Handiwork"] >= 3));
        for (var i = 1; i < result.Count; i++)
            Assert.True(result[i - 1].BreedingPower <= result[i].BreedingPower);
    }

    [Fact]
    public void Rarity_RangeIsInclusive()
    {
        var ids = Ids(new PalFilter { MinRarity = 5, MaxRarity = 8 });
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.InRange(Data.Pal(id).Rarity, 5, 8));
        Assert.Contains(Data.Pals.First(p => p.Rarity == 5).Id, ids);
        Assert.Contains(Data.Pals.First(p => p.Rarity == 8).Id, ids);

        Assert.All(Ids(new PalFilter { MinRarity = 10 }), id => Assert.True(Data.Pal(id).Rarity >= 10));
        Assert.All(Ids(new PalFilter { MaxRarity = 2 }), id => Assert.True(Data.Pal(id).Rarity <= 2));
    }

    [Fact]
    public void Variant_FilterSplitsTheList()
    {
        var variants = Ids(new PalFilter { Variant = true });
        var normal = Ids(new PalFilter { Variant = false });
        Assert.Equal(85, variants.Count);
        Assert.Equal(299, variants.Count + normal.Count);
        Assert.All(variants, id => Assert.True(Data.Pal(id).IsVariant));
    }

    [Fact]
    public void Nocturnal_FilterWorks()
    {
        var night = Ids(new PalFilter { Nocturnal = true });
        var day = Ids(new PalFilter { Nocturnal = false });
        Assert.NotEmpty(night);
        Assert.Equal(299, night.Count + day.Count);
        Assert.All(night, id => Assert.True(Data.Pal(id).Nocturnal));
    }

    [Fact]
    public void SameSpeciesOnly_FindsJetragon()
    {
        var ids = Ids(new PalFilter { SameSpeciesOnly = true });
        Assert.Contains("JetDragon", ids);
        Assert.DoesNotContain("Sekhmet", ids);
        Assert.Equal(24, ids.Count);
        Assert.DoesNotContain("JetDragon", Ids(new PalFilter { SameSpeciesOnly = false }));
    }

    [Fact]
    public void WildCatchable_UsesGameDataRule()
    {
        var wild = Ids(new PalFilter { WildCatchable = true });
        var notWild = Ids(new PalFilter { WildCatchable = false });
        Assert.Equal(295, wild.Count);
        Assert.Equal(299, wild.Count + notWild.Count);
        Assert.Contains("Sekhmet", wild);
        Assert.All(notWild, id => Assert.False(Data.IsWildCatchable(id)));
    }

    [Fact]
    public void Elements_MatchAnyOfTheSelected()
    {
        // Start from data without elements, then give three species known elements.
        var overrides = Real.Value.Pals.ToDictionary(p => p.Id, _ => Array.Empty<string>());
        overrides["Sekhmet"] = ["Ground"];
        overrides["CowPal"] = ["Normal"];
        overrides["JetDragon"] = ["Dragon", "Fire"];
        var data = WithElements(overrides);

        Assert.True(PalQuery.HasElementData(data));

        List<string> Run(params string[] elements) =>
            PalQuery.Run(data, new PalFilter { Elements = elements }, new PalSort(), "en").Select(p => p.Id).ToList();

        Assert.Equal(["CowPal", "Sekhmet"], Run("Normal", "Ground").OrderBy(x => x).ToList());
        Assert.Equal(["JetDragon"], Run("fire"));
        Assert.Equal(["JetDragon"], Run("Dragon"));
        Assert.Empty(Run("Water"));
    }

    [Fact]
    public void ElementFilter_UsesTheGameElements()
    {
        Assert.True(PalQuery.HasElementData(Data));
        var earth = PalQuery.Run(Data, new PalFilter { Elements = ["Earth"] }, new PalSort(), "en").Select(p => p.Id).ToList();
        Assert.Contains("Anubis", earth);
        Assert.All(earth, id => Assert.Contains("Earth", Data.Pal(id).Elements));
        Assert.Equal(Data.Pals.Count(p => p.Elements.Contains("Earth")), earth.Count);
    }

    [Fact]
    public void ObtainMethodFilter_MatchesAnyAndOnly()
    {
        List<string> Run(PalFilter f) => PalQuery.Run(Data, f, new PalSort(), "en").Select(p => p.Id).ToList();

        var alpha = Run(new PalFilter { ObtainMethods = ["AlphaBoss"] });
        Assert.Contains("Anubis", alpha);
        Assert.Contains("JetDragon", alpha);

        // Found as an Alpha boss and nothing else (besides breeding): Jetragon yes, Anubis (also wild) no.
        var only = Run(new PalFilter { ObtainMethods = ["AlphaBoss"], ObtainOnly = true });
        Assert.Contains("JetDragon", only);
        Assert.DoesNotContain("Anubis", only);
        Assert.All(only, id => Assert.All(Data.Pal(id).Obtain!.Methods.Where(m => m != "SameSpeciesOnly" && m != "Recipe"),
            m => Assert.Equal("AlphaBoss", m)));

        var either = Run(new PalFilter { ObtainMethods = ["Cage", "Fishing"] });
        Assert.All(either, id => Assert.True(Data.Pal(id).Obtain!.HasMethod("Cage") || Data.Pal(id).Obtain!.HasMethod("Fishing")));
        Assert.False(new PalFilter { ObtainMethods = ["Wild"] }.IsEmpty);
    }

    public static IEnumerable<object[]> NumericSortKeys() =>
        new[]
        {
            PalSortKey.DexNo, PalSortKey.Rarity, PalSortKey.BreedingPower, PalSortKey.Hp, PalSortKey.Attack,
            PalSortKey.Defense, PalSortKey.WalkSpeed, PalSortKey.RunSpeed, PalSortKey.RideSprintSpeed,
            PalSortKey.TransportSpeed, PalSortKey.Stamina, PalSortKey.MaxFullStomach, PalSortKey.FoodAmount,
            PalSortKey.CraftSpeed, PalSortKey.Price,
        }.Select(k => new object[] { k });

    private static int Value(PalSpecies p, PalSortKey key) => key switch
    {
        PalSortKey.DexNo => p.DexNo,
        PalSortKey.Rarity => p.Rarity,
        PalSortKey.BreedingPower => p.BreedingPower,
        PalSortKey.Hp => p.Stats.Hp,
        PalSortKey.Attack => p.Stats.Attack,
        PalSortKey.Defense => p.Stats.Defense,
        PalSortKey.WalkSpeed => p.Stats.WalkSpeed,
        PalSortKey.RunSpeed => p.Stats.RunSpeed,
        PalSortKey.RideSprintSpeed => p.Stats.RideSprintSpeed,
        PalSortKey.TransportSpeed => p.Stats.TransportSpeed,
        PalSortKey.Stamina => p.Stats.Stamina,
        PalSortKey.MaxFullStomach => p.Stats.MaxFullStomach,
        PalSortKey.FoodAmount => p.Stats.FoodAmount,
        PalSortKey.CraftSpeed => p.Stats.CraftSpeed,
        PalSortKey.Price => p.Price,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    [Theory]
    [MemberData(nameof(NumericSortKeys))]
    public void NumericSort_AscendingAndDescending(PalSortKey key)
    {
        var asc = PalQuery.Run(Data, new PalFilter(), new PalSort { Key = key }, "en");
        var desc = PalQuery.Run(Data, new PalFilter(), new PalSort { Key = key, Descending = true }, "en");

        Assert.Equal(299, asc.Count);
        Assert.Equal(299, desc.Count);
        for (var i = 1; i < asc.Count; i++) Assert.True(Value(asc[i - 1], key) <= Value(asc[i], key), "asc " + key);
        for (var i = 1; i < desc.Count; i++) Assert.True(Value(desc[i - 1], key) >= Value(desc[i], key), "desc " + key);
    }

    [Fact]
    public void WorkSort_UsesTheChosenWorkType()
    {
        var sort = new PalSort { Key = PalSortKey.Work, WorkType = "Mining", Descending = true };
        var result = PalQuery.Run(Data, new PalFilter(), sort, "en");
        int Level(PalSpecies p) => p.Work.GetValueOrDefault("Mining");
        for (var i = 1; i < result.Count; i++) Assert.True(Level(result[i - 1]) >= Level(result[i]));
        Assert.True(Level(result[0]) > 0);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void NameSort_FollowsTheLanguage(string lang)
    {
        var result = PalQuery.Run(Data, new PalFilter(), new PalSort { Key = PalSortKey.Name }, lang);
        var comparer = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo(lang), ignoreCase: true);
        for (var i = 1; i < result.Count; i++)
            Assert.True(comparer.Compare(result[i - 1].Names.Get(lang), result[i].Names.Get(lang)) <= 0);

        var desc = PalQuery.Run(Data, new PalFilter(), new PalSort { Key = PalSortKey.Name, Descending = true }, lang);
        Assert.Equal(result.Select(p => p.Names.Get(lang)).Reverse().OrderByDescending(x => x, comparer).First(),
            desc[0].Names.Get(lang));
    }

    [Fact]
    public void Sort_BreaksTiesByDexNumber()
    {
        var result = PalQuery.Run(Data, new PalFilter(), new PalSort { Key = PalSortKey.Rarity }, "en");
        for (var i = 1; i < result.Count; i++)
        {
            if (result[i - 1].Rarity == result[i].Rarity)
                Assert.True(result[i - 1].DexNo <= result[i].DexNo);
        }
    }

    [Fact]
    public void Filters_Combine()
    {
        var filter = new PalFilter { Variant = false, Nocturnal = true, MinRarity = 3 };
        var ids = Ids(filter);
        Assert.All(ids, id =>
        {
            var p = Data.Pal(id);
            Assert.False(p.IsVariant);
            Assert.True(p.Nocturnal);
            Assert.True(p.Rarity >= 3);
        });
        Assert.False(filter.IsEmpty);
    }

    [Fact]
    public void RankParentPairs_PutsClosestAveragePowerFirst()
    {
        var child = Data.Pal("Sekhmet");
        var ranked = PalQuery.RankParentPairs(Data, child, Breeding.Value.GetParents("Sekhmet"), "en");

        Assert.Equal(141, ranked.Count);
        double Distance(ParentPair p) =>
            Math.Abs((Data.Pal(p.A).BreedingPower + Data.Pal(p.B).BreedingPower) / 2.0 - child.BreedingPower);
        for (var i = 1; i < ranked.Count; i++) Assert.True(Distance(ranked[i - 1]) <= Distance(ranked[i]));
    }

    // ---- ownership (T39) ---------------------------------------------------------------------------------------------

    private static Dictionary<string, Paldium.Core.Owned.OwnedSpeciesStats> OwnedCounts() => new()
    {
        ["CowPal"] = new(3, 2, 1),   // a pair
        ["ChickenPal"] = new(2, 2, 0),      // females missing
        ["Anubis"] = new(1, 0, 0),      // gender unknown
    };

    private static List<string> OwnedIds(PalOwnership ownership, PalSort? sort = null) =>
        PalQuery.Run(Data, new PalFilter { Ownership = ownership }, sort ?? new PalSort(), "en", OwnedCounts()).Select(p => p.Id).ToList();

    [Fact]
    public void OwnershipFilters_UseTheOwnedCounts()
    {
        Assert.Equal(new[] { "ChickenPal", "Anubis", "CowPal" }.Order().ToList(), OwnedIds(PalOwnership.Owned).Order().ToList());
        Assert.Equal(Data.Pals.Count - 3, OwnedIds(PalOwnership.NotOwned).Count);
        Assert.DoesNotContain("CowPal", OwnedIds(PalOwnership.NotOwned));
        Assert.Equal(["CowPal"], OwnedIds(PalOwnership.Pair));
        Assert.Equal(new[] { "Anubis", "ChickenPal" }.Order().ToList(), OwnedIds(PalOwnership.MissingGender).Order().ToList());
    }

    [Fact]
    public void WithoutOwnedCounts_TheOwnershipFiltersDoNothing_AndTheOwnedSortFallsBackToDexNo()
    {
        var plain = Ids();
        Assert.Equal(plain, PalQuery.Run(Data, new PalFilter { Ownership = PalOwnership.Owned }, new PalSort(), "en").Select(p => p.Id).ToList());
        Assert.Equal(plain, PalQuery.Run(Data, new PalFilter(), new PalSort { Key = PalSortKey.OwnedCount, Descending = true }, "en").Select(p => p.Id).ToList());
    }

    [Fact]
    public void OwnedCountSort_OrdersByTheCount_TiesByDexNo()
    {
        var descending = OwnedIds(PalOwnership.Owned, new PalSort { Key = PalSortKey.OwnedCount, Descending = true });
        Assert.Equal(["CowPal", "ChickenPal", "Anubis"], descending);

        var all = PalQuery.Run(Data, new PalFilter(), new PalSort { Key = PalSortKey.OwnedCount, Descending = true }, "en", OwnedCounts());
        Assert.Equal(Data.Pals.Count, all.Count);
        Assert.Equal("CowPal", all[0].Id);
        // the not owned ones keep dex order at the end
        var rest = all.Skip(3).ToList();
        Assert.Equal(rest.OrderBy(p => p.DexNo).ThenBy(p => p.IsVariant).ThenBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Id), rest.Select(p => p.Id));
    }

    [Fact]
    public void AFilterWithOwnership_IsNotEmpty()
    {
        Assert.True(new PalFilter().IsEmpty);
        Assert.False(new PalFilter { Ownership = PalOwnership.Pair }.IsEmpty);
    }}
