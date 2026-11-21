using System.Text;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Tests;

[Collection("Heavy")]
public class BreedingRulesTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static readonly Lazy<BreedingCalculator> Table = new(() => BreedingCalculator.Load(Data.Value, TestPaths.DataFolder));
    private static readonly Lazy<BreedingRules> Rules = new(() => BreedingRules.Load(TestPaths.DataFolder));

    // Every pair of the PalCalc table (and each gender specific row) must give the same child from the game's rules.
    private static (int Rows, List<string> Differences) Compare()
    {
        var ids = Data.Value.Pals.Select(p => p.Id).Order(StringComparer.Ordinal).ToList();
        var rows = 0;
        var differences = new List<string>();
        for (var i = 0; i < ids.Count; i++)
        {
            for (var j = i; j < ids.Count; j++)
            {
                var outcomes = Table.Value.GetChildOutcomes(ids[i], ids[j]);
                foreach (var outcome in outcomes)
                {
                    rows++;
                    Gender? ga = outcome.AGender == GenderReq.Any ? null : outcome.AGender == GenderReq.Male ? Gender.Male : Gender.Female;
                    Gender? gb = outcome.BGender == GenderReq.Any ? null : outcome.BGender == GenderReq.Male ? Gender.Male : Gender.Female;
                    var computed = Rules.Value.FindChild(ids[i], ids[j], ga, gb);
                    if (!string.Equals(computed, outcome.ChildId, StringComparison.Ordinal))
                        differences.Add($"{ids[i]} + {ids[j]} ({outcome.AGender}/{outcome.BGender}): PalCalc {outcome.ChildId}, rules {computed}");
                }
            }
        }

        return (rows, differences);
    }

    [Fact]
    public void BreedingTable_IsExactlyWhatTheRulesGive()
    {
        // data/breeding.json is generated from the rule engine: every row must equal it.
        var (rows, differences) = Compare();
        Assert.True(rows >= 44850, rows.ToString());
        Assert.True(differences.Count == 0, $"{differences.Count} of {rows} rows differ, first: {string.Join(" | ", differences.Take(5))}");
    }

    // The only rows where the game rules differ from PalCalc's "same species" shortcut (PM decision, T22).
    private static readonly string[] AllowedPalCalcDifferences =
    [
        "KingWhale + KingWhale: PalCalc KingWhale, rules DomeArmorDragon",
        "PlantSlime_Flower + PlantSlime_Flower: PalCalc PlantSlime_Flower, rules PlantSlime",
    ];

    [Fact]
    public void RulesAgreeWithPalCalc_ExceptTheTwoAllowedSelfPairs()
    {
        var palcalc = new Paldium.DataBuilder.PalCalcSource(Path.Combine(TestPaths.RepoRoot, "external", "palcalc")).BuildPalCalcBreeding();
        var ids = palcalc.PalOrder;
        var n = ids.Count;
        var differences = new List<string>();
        for (var i = 0; i < n; i++)
            for (var j = i; j < n; j++)
            {
                var theirs = ids[palcalc.Children[i * n - i * (i - 1) / 2 + (j - i)]];
                foreach (var outcome in Table.Value.GetChildOutcomes(ids[i], ids[j]))
                {
                    // Gender specific pairs: PalCalc's matrix holds its default, compare through the gender rows below.
                    if (outcome.AGender != GenderReq.Any || outcome.BGender != GenderReq.Any) continue;
                    if (outcome.ChildId != theirs) differences.Add($"{ids[i]} + {ids[j]}: PalCalc {theirs}, rules {outcome.ChildId}");
                }
            }

        Assert.Equal(AllowedPalCalcDifferences.Order(StringComparer.Ordinal), differences.Order(StringComparer.Ordinal));
        Assert.Equal(
            palcalc.GenderSpecific.Select(g => $"{g.A}:{g.AGender}+{g.B}:{g.BGender}={g.Child}").Order(StringComparer.Ordinal),
            Data.Value.Pals.Count == 0 ? [] : GenderRows().Order(StringComparer.Ordinal));
    }

    private static IEnumerable<string> GenderRows() =>
        JsonSerializerOptionsHelper.Read(TestPaths.DataFolder).GenderSpecific.Select(g => $"{g.A}:{g.AGender}+{g.B}:{g.BGender}={g.Child}");

    [Fact]
    public void KingWhaleAndFlowerSlime_AreUnbreedable()
    {
        foreach (var id in new[] { "KingWhale", "PlantSlime_Flower" })
        {
            var pal = Data.Value.Pal(id);
            Assert.True(pal.BreedingFlags.Unbreedable);
            Assert.Equal(0, pal.BreedingFlags.ParentPairCount);
            Assert.False(pal.BreedingFlags.SameSpeciesOnly);
            Assert.Empty(Table.Value.GetParents(id));
        }

        Assert.Equal(2, Data.Value.Pals.Count(p => p.BreedingFlags.Unbreedable));
    }

    [Fact]
    public void RecipeChildren_AreFlaggedInTheBundle()
    {
        var children = Rules.Value.RecipeChildren.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pal in Data.Value.Pals)
        {
            Assert.Equal(children.Contains(pal.Id), pal.BreedingFlags.UniqueRecipe == true);
            Assert.Equal(children.Contains(pal.Id), pal.Obtain!.HasMethod("Recipe"));
        }

        Assert.Equal(753, Rules.Value.Rows.Count);
    }
}

internal static class JsonSerializerOptionsHelper
{
    public static Paldium.Core.Data.Schema.BreedingFile Read(string folder) =>
        System.Text.Json.JsonSerializer.Deserialize<Paldium.Core.Data.Schema.BreedingFile>(
            File.ReadAllBytes(Path.Combine(folder, "breeding.json")), Paldium.Core.Data.Schema.SchemaJson.Compact)!;
}
