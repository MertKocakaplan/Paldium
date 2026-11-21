using Paldium.App.Services;
using Paldium.Core.Planning;

namespace Paldium.App.Pages.PlannerParts;

/// <summary>The exclusions of a planner query (T107): single Pals (UID), species and groups, as stored in <see cref="PlannerSettings"/>.</summary>
public static class ExclusionOps
{
    public static int Count(PlannerSettings q) => q.ExcludedUids.Count + q.ExcludedSpecies.Count + q.ExcludedGroups.Count;

    public static bool Has(PlannerSettings q, PlanExclusion e) => e.Kind switch
    {
        PlanExclusion.UidKind => q.ExcludedUids.Contains(e.Id, StringComparer.Ordinal),
        PlanExclusion.SpeciesKind => q.ExcludedSpecies.Contains(e.Id, StringComparer.OrdinalIgnoreCase),
        _ => q.ExcludedGroups.Contains(e.Id, StringComparer.OrdinalIgnoreCase),
    };

    public static PlannerSettings With(PlannerSettings q, PlanExclusion e)
    {
        if (Has(q, e)) return q;
        return e.Kind switch
        {
            PlanExclusion.UidKind => q with { ExcludedUids = [.. q.ExcludedUids, e.Id] },
            PlanExclusion.SpeciesKind => q with { ExcludedSpecies = [.. q.ExcludedSpecies, e.Id] },
            _ => q with { ExcludedGroups = [.. q.ExcludedGroups, e.Id] },
        };
    }

    public static PlannerSettings Without(PlannerSettings q, PlanExclusion e) => e.Kind switch
    {
        PlanExclusion.UidKind => q with { ExcludedUids = q.ExcludedUids.Where(x => x != e.Id).ToList() },
        PlanExclusion.SpeciesKind => q with { ExcludedSpecies = q.ExcludedSpecies.Where(x => !string.Equals(x, e.Id, StringComparison.OrdinalIgnoreCase)).ToList() },
        _ => q with { ExcludedGroups = q.ExcludedGroups.Where(x => !string.Equals(x, e.Id, StringComparison.OrdinalIgnoreCase)).ToList() },
    };

    public static PlannerSettings Clear(PlannerSettings q) => q with { ExcludedUids = [], ExcludedSpecies = [], ExcludedGroups = [] };

    /// <summary>The list order of the exclusion list: groups, species (by name), Pals (as added).</summary>
    public static IReadOnlyList<PlanExclusion> InListOrder(PlannerSettings q, Func<string, string> speciesName) =>
    [
        .. q.ExcludedGroups.Select(g => new PlanExclusion(PlanExclusion.GroupKind, g)),
        .. q.ExcludedSpecies.OrderBy(speciesName, StringComparer.CurrentCultureIgnoreCase).Select(s => new PlanExclusion(PlanExclusion.SpeciesKind, s)),
        .. q.ExcludedUids.Select(u => new PlanExclusion(PlanExclusion.UidKind, u)),
    ];

    /// <summary>The Turkish accusative suffix after an apostrophe ("Hoocrates'i", "Nox'u", "Chikipi'yi") by the last vowel of the name.</summary>
    public static string Accusative(string name, string lang)
    {
        if (lang != "tr" || string.IsNullOrEmpty(name)) return name;
        const string vowels = "aeıioöuüAEIİOÖUÜ";
        var last = name.LastOrDefault(c => vowels.Contains(c));
        // Names of foreign Pals: the last letter decides how it is read; "x" and consonant endings take a plain vowel.
        var endsInVowel = vowels.Contains(name[^1]);
        var suffix = char.ToLowerInvariant(last) switch
        {
            'a' or 'ı' => "ı",
            'e' or 'i' => "i",
            'o' or 'u' => "u",
            'ö' or 'ü' => "ü",
            _ => "i",
        };
        return name + "'" + (endsInVowel ? "y" : "") + suffix;
    }
}
