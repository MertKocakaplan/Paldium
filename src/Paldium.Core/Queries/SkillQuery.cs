using System.Globalization;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Queries;

public enum PassiveSortKey { Rank, Name }

public sealed record PassiveSort
{
    /// <summary>Default: best passives first.</summary>
    public PassiveSortKey Key { get; init; } = PassiveSortKey.Rank;

    public bool Descending { get; init; } = true;
}

/// <summary>
/// Passive skill filter. Filters combine with AND. <see cref="Tags"/> only looks at the categories a passive
/// improves (<see cref="PassiveSkill.Tags"/>), never at its penalties.
/// </summary>
public sealed record PassiveFilter
{
    /// <summary>Matches the name (all languages), the description and the id.</summary>
    public string? Text { get; init; }

    /// <summary>Tags to match against <see cref="PassiveSkill.Tags"/>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>true: the passive must carry all <see cref="Tags"/>; false: any one of them is enough.</summary>
    public bool TagsMatchAll { get; init; }

    /// <summary>Exact rank values to include (5..-3); empty means all ranks.</summary>
    public IReadOnlyList<int> Ranks { get; init; } = [];

    public int? MinRank { get; init; }
    public int? MaxRank { get; init; }

    public bool? CanAppearRandomly { get; init; }
    public bool? SurgerySupported { get; init; }
    public bool? MutationOnly { get; init; }
}

public enum ActiveSortKey { Name, Power, Cooldown }

public sealed record ActiveSort
{
    public ActiveSortKey Key { get; init; } = ActiveSortKey.Name;
    public bool Descending { get; init; }
}

/// <summary>Active skill filter. Filters combine with AND.</summary>
public sealed record ActiveFilter
{
    public string? Text { get; init; }

    /// <summary>The skill must have one of these elements.</summary>
    public IReadOnlyList<string> Elements { get; init; } = [];

    public int? MinPower { get; init; }
    public int? MaxPower { get; init; }

    /// <summary>true: only skills a Pal can inherit, false: only the others.</summary>
    public bool? Inheritable { get; init; }

    /// <summary>true: only skills that exist as a skill fruit.</summary>
    public bool? HasSkillFruit { get; init; }
}

/// <summary>Filtering and sorting of the passive and active skill lists, independent of the UI.</summary>
public static class SkillQuery
{
    public static IReadOnlyList<PassiveSkill> Passives(IGameData data, PassiveFilter filter, PassiveSort sort, string lang)
    {
        var filtered = data.Passives.Where(p => Matches(p, filter));
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo(lang), ignoreCase: true);

        IOrderedEnumerable<PassiveSkill> ordered = sort.Key == PassiveSortKey.Name
            ? (sort.Descending ? filtered.OrderByDescending(p => p.Names.Get(lang), comparer) : filtered.OrderBy(p => p.Names.Get(lang), comparer))
            : (sort.Descending ? filtered.OrderByDescending(p => p.Rank) : filtered.OrderBy(p => p.Rank));

        return ordered
            .ThenBy(p => p.Names.Get(lang), comparer)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
    }

    public static bool Matches(PassiveSkill passive, PassiveFilter f)
    {
        if (f.Ranks.Count > 0 && !f.Ranks.Contains(passive.Rank)) return false;
        if (f.MinRank is { } min && passive.Rank < min) return false;
        if (f.MaxRank is { } max && passive.Rank > max) return false;

        if (f.CanAppearRandomly is { } random && passive.CanAppearRandomly != random) return false;
        if (f.SurgerySupported is { } surgery && passive.Surgery.Supported != surgery) return false;
        if (f.MutationOnly is { } mutation && passive.MutationExclusive != mutation) return false;

        if (f.Tags.Count > 0)
        {
            var matchedTags = f.TagsMatchAll
                ? f.Tags.All(t => passive.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                : passive.Tags.Any(t => f.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
            if (!matchedTags) return false;
        }

        if (string.IsNullOrWhiteSpace(f.Text)) return true;
        var q = f.Text.Trim();
        var folded = TextFold.Fold(q);
        return ContainsAnyLanguage(passive.Names, folded) ||
               ContainsAnyLanguage(passive.Descriptions, folded) ||
               TextFold.Fold(passive.Id).Contains(folded, StringComparison.Ordinal);
    }

    public static IReadOnlyList<ActiveSkill> Actives(IGameData data, ActiveFilter filter, ActiveSort sort, string lang)
    {
        var filtered = data.Actives.Where(a => Matches(a, filter));
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo(lang), ignoreCase: true);

        IOrderedEnumerable<ActiveSkill> ordered = sort.Key switch
        {
            ActiveSortKey.Power => sort.Descending ? filtered.OrderByDescending(a => a.Power) : filtered.OrderBy(a => a.Power),
            ActiveSortKey.Cooldown => sort.Descending ? filtered.OrderByDescending(a => a.CooldownSeconds) : filtered.OrderBy(a => a.CooldownSeconds),
            _ => sort.Descending ? filtered.OrderByDescending(a => a.Names.Get(lang), comparer) : filtered.OrderBy(a => a.Names.Get(lang), comparer),
        };

        return ordered
            .ThenBy(a => a.Names.Get(lang), comparer)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
    }

    public static bool Matches(ActiveSkill skill, ActiveFilter f)
    {
        if (f.Elements.Count > 0 && !f.Elements.Contains(skill.Element, StringComparer.OrdinalIgnoreCase)) return false;
        if (f.MinPower is { } minP && skill.Power < minP) return false;
        if (f.MaxPower is { } maxP && skill.Power > maxP) return false;
        if (f.Inheritable is { } inheritable && skill.CanInherit != inheritable) return false;
        if (f.HasSkillFruit is { } fruit && skill.HasSkillFruit != fruit) return false;

        if (string.IsNullOrWhiteSpace(f.Text)) return true;
        var q = f.Text.Trim();
        var folded = TextFold.Fold(q);
        return ContainsAnyLanguage(skill.Names, folded) || TextFold.Fold(skill.Id).Contains(folded, StringComparison.Ordinal);
    }

    // The query is already folded.
    private static bool ContainsAnyLanguage(LocalizedText text, string foldedQuery)
    {
        foreach (var lang in text.Languages)
        {
            if (TextFold.Fold(text.Get(lang)).Contains(foldedQuery, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
