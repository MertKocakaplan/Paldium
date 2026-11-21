using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.App.Pages.TechnologyParts;

/// <summary>The search and the two filter pills of the Technology page (screens/technology.md).</summary>
public sealed record TechFilter(string Query = "", bool AncientOnly = false, bool PrerequisiteOnly = false)
{
    public bool Any => !string.IsNullOrWhiteSpace(Query) || AncientOnly || PrerequisiteOnly;
}

/// <summary>One level row: the technologies shown in it and the level's point totals (all of its technologies, not only the shown ones).</summary>
public sealed record TechLevel(int Level, IReadOnlyList<TechnologyInfo> Technologies, int Points, int AncientPoints);

/// <summary>
/// The technology tree page (T128, game data only): level rows, search and filters, and the links between technologies. Pure functions over the data bundle,
/// tested without the page.
/// </summary>
public static class TechnologyModel
{
    /// <summary>The levels of the jump bar on the right edge.</summary>
    public static readonly IReadOnlyList<int> JumpLevels = [1, 10, 20, 30, 40, 50, 60, 70, 80];

    /// <summary>Has a prerequisite: a tower boss, a research or an earlier technology.</summary>
    public static bool HasPrerequisite(TechnologyInfo t) =>
        t.Requires is { } r && (r.TechnologyId != null || r.TowerBossType != null || r.ResearchId != null);

    /// <summary>
    /// The search looks at the technology's name and at the names of what it unlocks (so "sphere" finds Pal Sphere and the Sphere Workbench), in the UI language
    /// and in English.
    /// </summary>
    public static bool Matches(TechnologyInfo t, TechFilter filter, string lang)
    {
        if (filter.AncientOnly && !t.Ancient) return false;
        if (filter.PrerequisiteOnly && !HasPrerequisite(t)) return false;
        var query = TextFold.Fold(filter.Query?.Trim() ?? "");
        if (query.Length == 0) return true;
        if (TextFold.Fold(t.Names.Get(lang) + " " + t.Names.Get("en")).Contains(query, StringComparison.Ordinal)) return true;
        return t.Unlocks.Any(u => TextFold.Fold(u.Name.Get(lang) + " " + u.Name.Get("en")).Contains(query, StringComparison.Ordinal));
    }

    /// <summary>The level rows (ascending). With a search or a filter, technologies that do not match and levels left empty are left out.</summary>
    public static IReadOnlyList<TechLevel> Levels(IEnumerable<TechnologyInfo> all, TechFilter filter, string lang)
    {
        var list = all.ToList();
        var result = new List<TechLevel>();
        foreach (var group in list.GroupBy(t => t.Level).OrderBy(g => g.Key))
        {
            var shown = group.OrderBy(t => t.Index).Where(t => Matches(t, filter, lang)).ToList();
            if (shown.Count == 0) continue;
            result.Add(new TechLevel(group.Key, shown, group.Where(t => !t.Ancient).Sum(t => t.Cost), group.Where(t => t.Ancient).Sum(t => t.Cost)));
        }

        return result;
    }

    /// <summary>The level the jump bar scrolls to: the wanted one, or the next shown one when it is hidden by a filter (the last one when none follows).</summary>
    public static int? JumpTarget(IReadOnlyList<TechLevel> levels, int wanted) =>
        levels.Count == 0 ? null : (levels.FirstOrDefault(l => l.Level >= wanted) ?? levels[^1]).Level;

    /// <summary>The picture of a technology: its own icon, or the first icon among what it unlocks (a few sets have none of their own).</summary>
    public static string? IconOf(TechnologyInfo t) => t.IconPath ?? t.Unlocks.Select(u => u.IconPath).FirstOrDefault(p => p != null);

    /// <summary>The technologies that need this one first ("Leads to").</summary>
    public static IReadOnlyList<TechnologyInfo> LeadsTo(IEnumerable<TechnologyInfo> all, TechnologyInfo t) =>
        all.Where(x => string.Equals(x.Requires?.TechnologyId, t.Id, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.Level).ThenBy(x => x.Index).ToList();
}
