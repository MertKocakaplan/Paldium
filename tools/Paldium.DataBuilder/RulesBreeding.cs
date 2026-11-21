using Paldium.Core.Breeding;
using Paldium.Core.Data.Schema;
using Paldium.Core.Domain;

namespace Paldium.DataBuilder;

/// <summary>
/// Builds the breeding table (<c>breeding.json</c>) from the game's own rules (<see cref="BreedingRules"/>), the single source of
/// truth since T22. The PalCalc table is only used as a cross-check (and as the fallback when no extractor data is given).
/// </summary>
public static class RulesBreeding
{
    public sealed record Result(BreedingFile File, int[] ParentPairCounts, bool[] SameSpeciesOnly);

    public static Result Build(BreedingRulesFile rulesFile, IReadOnlyList<string> palOrder)
    {
        var rules = new BreedingRules(rulesFile);
        var n = palOrder.Count;
        var index = palOrder.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        var tribeOf = rulesFile.Rows.ToDictionary(r => r.Id, r => r.Tribe, StringComparer.OrdinalIgnoreCase);

        // Species per tribe (bundle species only) to turn the tribe based recipes into species rows.
        var speciesOfTribe = palOrder.GroupBy(id => tribeOf[id], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Recipes that depend on genders: Female-first rows keep the table default stable (the matrix holds the first row's child).
        var genderRows = new List<GenderSpecificDto>();
        var genderSlots = new Dictionary<(int, int), string>();
        foreach (var recipe in rulesFile.Recipes.Where(r => r.GenderA != null || r.GenderB != null)
                     .OrderBy(r => r.GenderA == "Female" ? 0 : 1).ThenBy(r => r.Row, StringComparer.Ordinal))
        {
            if (!speciesOfTribe.TryGetValue(recipe.ParentA, out var listA) || !speciesOfTribe.TryGetValue(recipe.ParentB, out var listB)) continue;
            foreach (var a in listA)
                foreach (var b in listB)
                {
                    genderRows.Add(new GenderSpecificDto(a, recipe.GenderA ?? "Any", b, recipe.GenderB ?? "Any", recipe.Child));
                    var slot = index[a] <= index[b] ? (index[a], index[b]) : (index[b], index[a]);
                    genderSlots.TryAdd(slot, recipe.Child);
                }
        }

        int Slot(int i, int j)
        {
            if (i > j) (i, j) = (j, i);
            return i * n - i * (i - 1) / 2 + (j - i);
        }

        var matrix = new int[n * (n + 1) / 2];
        var pairsPerChild = new HashSet<(int, int)>[n];
        for (var i = 0; i < n; i++) pairsPerChild[i] = [];
        for (var i = 0; i < n; i++)
        {
            for (var j = i; j < n; j++)
            {
                var childId = genderSlots.TryGetValue((i, j), out var gendered)
                    ? gendered
                    : rules.FindChild(palOrder[i], palOrder[j]);
                if (!index.TryGetValue(childId, out var child))
                    throw new InvalidDataException($"Rules give '{childId}' for {palOrder[i]} + {palOrder[j]}, which is not a bundle species.");
                matrix[Slot(i, j)] = child;
                if (!genderSlots.ContainsKey((i, j))) pairsPerChild[child].Add((i, j));
            }
        }

        foreach (var row in genderRows)
        {
            var child = index[row.Child];
            pairsPerChild[child].Add((Math.Min(index[row.A], index[row.B]), Math.Max(index[row.A], index[row.B])));
        }

        var pairCounts = new int[n];
        var sameOnly = new bool[n];
        for (var i = 0; i < n; i++)
        {
            pairCounts[i] = pairsPerChild[i].Count;
            sameOnly[i] = pairsPerChild[i].Count == 1 && pairsPerChild[i].Single() is var (x, y) && x == y;
        }

        return new Result(new BreedingFile(palOrder, matrix, genderRows), pairCounts, sameOnly);
    }
}
