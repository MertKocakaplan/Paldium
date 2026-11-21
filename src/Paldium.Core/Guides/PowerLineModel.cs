using Paldium.Core.Breeding;

namespace Paldium.Core.Guides;

/// <summary>One species mark on the power axis: a candidate, or a species that only comes from a recipe.</summary>
public sealed record PowerLineMark(string SpeciesId, int Rank, bool IsCandidate);

/// <summary>
/// The power-line figure of the breeding guide: the parents' average, the species around it, the child and the first
/// candidate on the right (screens/guides.md). Everything comes from the game's breeding rules.
/// </summary>
public sealed record PowerLineModel(
    int RankA, int RankB, int Target, int AxisMin, int AxisMax,
    IReadOnlyList<PowerLineMark> Marks, string ChildId, int ChildRank, string? NextId, int NextRank)
{
    /// <summary>Half the width of the axis around the average.</summary>
    public const int Span = 160;

    public static PowerLineModel Build(BreedingRules rules, string parentA, string parentB)
    {
        var rankA = rules.RankOf(parentA);
        var rankB = rules.RankOf(parentB);
        var target = (rankA + rankB + 1) / 2;
        var min = target - Span;
        var max = target + Span;

        var marks = rules.Rows
            .Where(r => !r.IsBoss && !r.IgnoreCombi && r.CombiRank >= min && r.CombiRank <= max)
            .Select(r => new PowerLineMark(r.Id, r.CombiRank, !rules.RecipeChildren.Contains(r.Id)))
            .OrderBy(m => m.Rank)
            .ToList();

        var child = rules.FindChild(parentA, parentB);
        var childRank = rules.RankOf(child);
        var next = marks.Where(m => m.IsCandidate && m.Rank > target).OrderBy(m => m.Rank).FirstOrDefault();
        return new PowerLineModel(rankA, rankB, target, min, max, marks, child, childRank, next?.SpeciesId, next?.Rank ?? 0);
    }
}
