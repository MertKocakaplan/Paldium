using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Planning;

/// <summary>One exclusion of a plan query: "Uid", "Species" or "Group" with its id.</summary>
public sealed record PlanExclusion(string Kind, string Id)
{
    public const string UidKind = "Uid";
    public const string SpeciesKind = "Species";
    public const string GroupKind = "Group";
}

/// <summary>
/// Pals the user does not want in any plan (T107, ARCHITECTURE 13): single Pals (UID), species and groups (a group lists species through
/// <see cref="PalSpecies.Group"/>). The start Pal is exempt from the species and group exclusions (the user chose it by hand).
/// </summary>
public sealed class PlanExclusions
{
    public static readonly PlanExclusions None = new([], [], null);

    private readonly HashSet<string> _uids;
    private readonly string? _startUid;

    private PlanExclusions(HashSet<string> uids, HashSet<string> species, string? startUid)
    {
        _uids = uids;
        Species = species;
        _startUid = startUid;
    }

    /// <summary>Species the plan may not contain (own Pals, wild catches and breeding children), groups already expanded.</summary>
    public HashSet<string> Species { get; }

    public bool IsEmpty => _uids.Count == 0 && Species.Count == 0;

    public static PlanExclusions Create(
        IGameData data, IReadOnlyList<string>? uids, IReadOnlyList<string>? species, IReadOnlyList<string>? groups,
        StartParent? start, string targetId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in species ?? []) set.Add(id);
        if (groups is { Count: > 0 })
            foreach (var pal in data.Pals)
                if (pal.Group != null && groups.Contains(pal.Group, StringComparer.OrdinalIgnoreCase)) set.Add(pal.Id);
        // The target itself and the start species are never excluded.
        set.Remove(targetId);
        if (start?.SpeciesId != null) set.Remove(start.SpeciesId);
        var uidSet = new HashSet<string>(uids ?? [], StringComparer.Ordinal);
        if (start?.OwnedUid != null) uidSet.Remove(start.OwnedUid);
        return uidSet.Count == 0 && set.Count == 0 ? None : new PlanExclusions(uidSet, set, start?.OwnedUid);
    }

    public static PlanExclusions Create(IGameData data, GoalQuery q, string targetId) =>
        Create(data, q.ExcludedUids, q.ExcludedSpecies, q.ExcludedGroups, q.Start, targetId);

    public static PlanExclusions Create(IGameData data, PathQuery q, string targetId) =>
        Create(data, q.ExcludedUids, q.ExcludedSpecies, q.ExcludedGroups, q.Start, targetId);

    public bool ExcludesSpecies(string speciesId) => Species.Contains(speciesId);

    /// <summary>The owned Pals the plan may use: the start Pal always stays.</summary>
    public IReadOnlyList<OwnedPal> Filter(IReadOnlyList<OwnedPal> owned) =>
        IsEmpty ? owned : owned.Where(p => p.Uid == _startUid || (!_uids.Contains(p.Uid) && !Species.Contains(p.SpeciesId))).ToList();

    /// <summary>The query's exclusions in the order groups, species, Pals (the list the diagnosis walks, last ones first).</summary>
    public static IReadOnlyList<PlanExclusion> ListOf(IReadOnlyList<string>? uids, IReadOnlyList<string>? species, IReadOnlyList<string>? groups) =>
    [
        .. (groups ?? []).Select(g => new PlanExclusion(PlanExclusion.GroupKind, g)),
        .. (species ?? []).Select(s => new PlanExclusion(PlanExclusion.SpeciesKind, s)),
        .. (uids ?? []).Select(u => new PlanExclusion(PlanExclusion.UidKind, u)),
    ];
}

/// <summary>An exclusion whose removal lets the planner find a plan (T107 diagnosis): the best plan without it.</summary>
public sealed record ExclusionBlocker(PlanExclusion Exclusion, double Eggs, int Steps);
