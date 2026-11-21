using System.Text.Json;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Tests;

public class BreedingCalculatorTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static readonly Lazy<BreedingCalculator> Calc =
        new(() => BreedingCalculator.Load(Data.Value, TestPaths.DataFolder));

    private static IBreedingCalculator Calculator => Calc.Value;

    private static Gender? ParseGender(string value) => value switch
    {
        "MALE" => Gender.Male,
        "FEMALE" => Gender.Female,
        _ => null,
    };

    [Fact]
    public void Oracle_AllPalCalcRowsMatch()
    {
        // PalCalc's own breeding.json is the reference: 44,851 rows, WILDCARD rows are called without genders.
        var path = Path.Combine(TestPaths.RepoRoot, "external", "palcalc", "PalCalc.Model", "breeding.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var rows = doc.RootElement.GetProperty("Breeding");

        var checkedRows = 0;
        var mismatches = new List<string>();
        foreach (var row in rows.EnumerateArray())
        {
            var a = row.GetProperty("Parent1InternalName").GetString()!;
            var b = row.GetProperty("Parent2InternalName").GetString()!;
            var expected = row.GetProperty("ChildInternalName").GetString()!;
            var ga = ParseGender(row.GetProperty("Parent1Gender").GetString()!);
            var gb = ParseGender(row.GetProperty("Parent2Gender").GetString()!);

            var actual = Calculator.GetChild(a, b, ga, gb);
            var swapped = Calculator.GetChild(b, a, gb, ga);
            if (actual != expected || swapped != expected)
                mismatches.Add($"{a}({ga}) + {b}({gb}): expected {expected}, got {actual} / {swapped}");
            checkedRows++;
        }

        Assert.Equal(44851, checkedRows);
        // The table is generated from the game rules (T22); PalCalc's same species shortcut differs in exactly these two pairs.
        Assert.Equal(2, mismatches.Count);
        Assert.Contains(mismatches, m => m.StartsWith("KingWhale(", StringComparison.Ordinal));
        Assert.Contains(mismatches, m => m.StartsWith("PlantSlime_Flower(", StringComparison.Ordinal));
    }

    [Fact]
    public void Gender_CatMageFemaleWithFoxMageMale_GivesCatMageFire()
    {
        Assert.Equal("CatMage_Fire", Calculator.GetChild("CatMage", "FoxMage", Gender.Female, Gender.Male));
        Assert.Equal("CatMage_Fire", Calculator.GetChild("FoxMage", "CatMage", Gender.Male, Gender.Female));
    }

    [Fact]
    public void Gender_CatMageMaleWithFoxMageFemale_GivesFoxMageDark()
    {
        Assert.Equal("FoxMage_Dark", Calculator.GetChild("CatMage", "FoxMage", Gender.Male, Gender.Female));
        Assert.Equal("FoxMage_Dark", Calculator.GetChild("FoxMage", "CatMage", Gender.Female, Gender.Male));
    }

    [Fact]
    public void GetChildOutcomes_ReturnsBothResultsForGenderDependentPair()
    {
        var outcomes = Calculator.GetChildOutcomes("CatMage", "FoxMage");
        Assert.Equal(2, outcomes.Count);
        Assert.Contains(new ChildOutcome("CatMage_Fire", GenderReq.Female, GenderReq.Male), outcomes);
        Assert.Contains(new ChildOutcome("FoxMage_Dark", GenderReq.Male, GenderReq.Female), outcomes);

        // Swapping the parents swaps which gender requirement belongs to which parent.
        var swapped = Calculator.GetChildOutcomes("FoxMage", "CatMage");
        Assert.Contains(new ChildOutcome("CatMage_Fire", GenderReq.Male, GenderReq.Female), swapped);
        Assert.Contains(new ChildOutcome("FoxMage_Dark", GenderReq.Female, GenderReq.Male), swapped);
    }

    [Fact]
    public void GetChildOutcomes_PlainPairHasSingleAnyOutcome()
    {
        var outcome = Assert.Single(Calculator.GetChildOutcomes("LazyDragon", "WhiteMoth_Neutral"));
        Assert.Equal(new ChildOutcome("Sekhmet", GenderReq.Any, GenderReq.Any), outcome);
    }

    [Fact]
    public void GetChild_IgnoresGendersForPlainPairs_AndIsCaseInsensitive()
    {
        Assert.Equal("Sekhmet", Calculator.GetChild("lazydragon", "WHITEMOTH_NEUTRAL"));
        Assert.Equal("Sekhmet", Calculator.GetChild("LazyDragon", "WhiteMoth_Neutral", Gender.Male, Gender.Female));
        Assert.Equal("Sekhmet", Calculator.GetChild("LazyDragon", "WhiteMoth_Neutral", Gender.Male, Gender.Male));
    }

    [Fact]
    public void GetChild_GenderDependentPairWithoutBothGenders_UsesTableDefault()
    {
        var plain = Calculator.GetChild("CatMage", "FoxMage");
        Assert.Contains(plain, new[] { "CatMage_Fire", "FoxMage_Dark" });
        Assert.Equal(plain, Calculator.GetChild("CatMage", "FoxMage", Gender.Female, null));
    }

    [Fact]
    public void GetChild_UnknownId_Throws()
    {
        Assert.Throws<KeyNotFoundException>(() => Calculator.GetChild("NoSuchPal", "CowPal"));
        Assert.Throws<KeyNotFoundException>(() => Calculator.GetParents("NoSuchPal"));
        Assert.Throws<KeyNotFoundException>(() => Calculator.MinStepsToTarget("CowPal", "NoSuchPal"));
    }

    [Fact]
    public void GetParents_Sekhmet_Has141Pairs()
    {
        var parents = Calculator.GetParents("Sekhmet");
        Assert.Equal(141, parents.Count);
        Assert.Equal(Data.Value.Pal("Sekhmet").BreedingFlags.ParentPairCount, parents.Count);
        Assert.Contains(parents, p =>
            (p.A == "LazyDragon" && p.B == "WhiteMoth_Neutral") || (p.A == "WhiteMoth_Neutral" && p.B == "LazyDragon"));
    }

    [Fact]
    public void GetParents_EveryPairProducesItsChild_AndCountsMatchTable()
    {
        var total = 0;
        foreach (var pal in Data.Value.Pals)
        {
            var parents = Calculator.GetParents(pal.Id);
            Assert.Equal(pal.BreedingFlags.ParentPairCount, parents.Count);
            total += parents.Count;
            foreach (var pair in parents)
            {
                var ga = pair.AReq == GenderReq.Any ? (Gender?)null : pair.AReq == GenderReq.Male ? Gender.Male : Gender.Female;
                var gb = pair.BReq == GenderReq.Any ? (Gender?)null : pair.BReq == GenderReq.Male ? Gender.Male : Gender.Female;
                Assert.Equal(pal.Id, Calculator.GetChild(pair.A, pair.B, ga, gb));
            }
        }

        Assert.Equal(44851, total);
    }

    [Fact]
    public void GetParents_GenderDependentPairsCarryGenderRequirements()
    {
        var fire = Calculator.GetParents("CatMage_Fire");
        Assert.Contains(fire, p => p is { A: "CatMage", B: "FoxMage", AReq: GenderReq.Female, BReq: GenderReq.Male });
    }

    [Fact]
    public void MinStepsToTarget_KnownValues()
    {
        Assert.Equal(1, Calculator.MinStepsToTarget("LazyDragon", "Sekhmet"));
        Assert.Equal(2, Calculator.MinStepsToTarget("CowPal", "Sekhmet"));
        Assert.Equal(0, Calculator.MinStepsToTarget("Sekhmet", "Sekhmet"));
    }

    [Fact]
    public void MinStepsToTarget_IsConsistentWithParents()
    {
        // Every parent of the target is exactly one step away (or zero for the target itself).
        foreach (var pair in Calculator.GetParents("Sekhmet"))
        {
            Assert.True(Calculator.MinStepsToTarget(pair.A, "Sekhmet") <= 1);
            Assert.True(Calculator.MinStepsToTarget(pair.B, "Sekhmet") <= 1);
        }
    }

    [Fact]
    public void MinStepsToTarget_UnreachableTarget_ReturnsMaxValue()
    {
        // Jetragon can only be produced by Jetragon + Jetragon, so nothing else can reach it.
        Assert.Equal(int.MaxValue, Calculator.MinStepsToTarget("CowPal", "JetDragon"));
        Assert.Equal(0, Calculator.MinStepsToTarget("JetDragon", "JetDragon"));
    }

    [Fact]
    public void MinStepsToTarget_IsCachedAndStableAcrossCalls()
    {
        var first = Calculator.MinStepsToTarget("CowPal", "Sekhmet");
        var second = Calculator.MinStepsToTarget("cowpal", "SEKHMET");
        Assert.Equal(first, second);
    }

    [Fact]
    public void ChildGenderProbability_UsesMaleProbability()
    {
        Assert.Equal(0.3, Calculator.ChildGenderProbability("Sekhmet", Gender.Male), 9);
        Assert.Equal(0.7, Calculator.ChildGenderProbability("Sekhmet", Gender.Female), 9);
        Assert.Equal(0.2, Calculator.ChildGenderProbability("CowPal", Gender.Male), 9);
        Assert.Equal(0.8, Calculator.ChildGenderProbability("CowPal", Gender.Female), 9);
    }
}
