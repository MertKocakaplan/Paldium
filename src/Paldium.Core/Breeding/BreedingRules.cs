using System.Text.Json;
using Paldium.Core.Data.Schema;
using Paldium.Core.Domain;

namespace Paldium.Core.Breeding;

/// <summary>One species row of the game's character table (<c>data/breeding-rules.json</c>).</summary>
public sealed record RuleRow(
    string Id, string Tribe, int CombiRank, int CombiDuplicatePriority, bool IsBoss, bool IgnoreCombi,
    bool IsTowerBoss, bool IsRaidBoss, int Order);

/// <summary>A special recipe of <c>DT_PalCombiUnique</c>; parents are tribes, genders are "Male", "Female" or null (any).</summary>
public sealed record RuleRecipe(string Row, string ParentA, string? GenderA, string ParentB, string? GenderB, string Child);

public sealed record BreedingRulesFile(IReadOnlyList<RuleRow> Rows, IReadOnlyList<RuleRecipe> Recipes);

/// <summary>
/// Child species of two parents computed from the game's rules (ARCHITECTURE 7.5): special recipe first, otherwise the
/// species whose breeding power is nearest to the parents' average (ties: higher priority, then table order).
/// </summary>
public sealed class BreedingRules
{
    private readonly Dictionary<string, RuleRow> _byId;
    private readonly IReadOnlyList<RuleRow> _rows;
    private readonly IReadOnlyList<RuleRecipe> _recipes;
    private readonly HashSet<string> _recipeChildren;

    public BreedingRules(BreedingRulesFile file)
    {
        _rows = file.Rows.OrderBy(r => r.Order).ToList();
        _recipes = file.Recipes;
        _byId = new Dictionary<string, RuleRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows) _byId[row.Id] = row;
        _recipeChildren = new HashSet<string>(_recipes.Select(r => r.Child), StringComparer.OrdinalIgnoreCase);
    }

    public static BreedingRules Load(string dataFolder)
    {
        var path = Path.Combine(dataFolder, "breeding-rules.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"Breeding rules not found: {path}", path);
        var file = JsonSerializer.Deserialize<BreedingRulesFile>(File.ReadAllBytes(path), SchemaJson.Compact)
                   ?? throw new InvalidDataException($"Breeding rules are empty: {path}");
        return new BreedingRules(file);
    }

    public IReadOnlyList<RuleRow> Rows => _rows;

    /// <summary>Species ids that are the child of a special recipe.</summary>
    public IReadOnlyCollection<string> RecipeChildren => _recipeChildren;

    public int RankOf(string speciesId) => Row(speciesId).CombiRank;

    private RuleRow Row(string id) =>
        _byId.TryGetValue(id, out var row) ? row : throw new KeyNotFoundException($"Unknown species row '{id}'.");

    /// <param name="rankBonus">Item breeding power adjustment (cake <c>combiRankBonus</c>, clamped to 0..10).</param>
    public string FindChild(string a, string b, Gender? genderA = null, Gender? genderB = null, int rankBonus = 0)
    {
        var rowA = Row(a);
        var rowB = Row(b);

        foreach (var recipe in _recipes)
        {
            if (Matches(recipe.ParentA, recipe.GenderA, rowA.Tribe, genderA) && Matches(recipe.ParentB, recipe.GenderB, rowB.Tribe, genderB)) return recipe.Child;
            if (Matches(recipe.ParentA, recipe.GenderA, rowB.Tribe, genderB) && Matches(recipe.ParentB, recipe.GenderB, rowA.Tribe, genderA)) return recipe.Child;
        }

        var target = (rowA.CombiRank + rowB.CombiRank + 1) / 2 + Math.Clamp(rankBonus, 0, 10);
        return Nearest(target, excludeRecipeChildren: true);
    }

    private static bool Matches(string recipeTribe, string? recipeGender, string tribe, Gender? gender) =>
        string.Equals(recipeTribe, tribe, StringComparison.OrdinalIgnoreCase) &&
        (recipeGender == null || (gender != null && string.Equals(recipeGender, gender.ToString(), StringComparison.Ordinal)));

    /// <summary>
    /// The eligible species with the breeding power nearest to <paramref name="target"/>: bosses and rows that ignore the
    /// combination are skipped, optionally also every recipe child. Ties go to the higher priority, then the earlier row.
    /// </summary>
    public string Nearest(int target, bool excludeRecipeChildren)
    {
        RuleRow? best = null;
        var bestDistance = int.MaxValue;
        foreach (var row in _rows)
        {
            if (row.IsBoss || row.IgnoreCombi) continue;
            if (excludeRecipeChildren && _recipeChildren.Contains(row.Id)) continue;
            var distance = Math.Abs(row.CombiRank - target);
            if (distance < bestDistance || (distance == bestDistance && row.CombiDuplicatePriority > best!.CombiDuplicatePriority))
            {
                best = row;
                bestDistance = distance;
            }
        }

        return best?.Id ?? throw new InvalidOperationException("No eligible species row.");
    }
}
