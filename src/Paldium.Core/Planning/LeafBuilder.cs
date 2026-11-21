using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.Core.Planning;

/// <summary>
/// A source of Pals of one species and gender at no breeding cost: owned Pals, or a wild leaf (Pals empty).
/// </summary>
/// <param name="Wild">1 for a wild leaf (also for an assumed start species), 0 for owned Pals.</param>
public sealed record LeafSource(
    string SpeciesId, Gender Gender, bool UsesStart, bool UsesPreferred, int Wild,
    IReadOnlyList<OwnedPal> Pals, string? ObtainMethod);

/// <summary>Builds the leaves of the planners (shared by the species path finder and the passive solver).</summary>
public static class LeafBuilder
{
    // Easiest way first (ARCHITECTURE 7.3).
    private static readonly string[] MethodPriority =
        ["Wild", "Cage", "Merchant", "Fishing", "Dungeon", "AlphaBoss", "Imprisoned", "DungeonBoss", "Raid"];

    public const int LegendaryRarity = 20;

    /// <summary>The easiest obtain method of a species other than breeding, or null when there is none.</summary>
    public static string? ObtainMethodOf(PalSpecies pal)
    {
        if (pal.Obtain == null) return pal.WildLevel != null ? "Wild" : null;
        return MethodPriority.FirstOrDefault(pal.Obtain.HasMethod);
    }

    /// <param name="scoped">Pals that already passed <see cref="OwnedPalRepository.Filter(IEnumerable{OwnedPal}, OwnerScope)"/>.</param>
    public static IReadOnlyList<LeafSource> Build(
        IGameData data, IReadOnlyList<OwnedPal> scoped, bool allowWild, bool excludeLegendary,
        StartParent? start, IReadOnlyCollection<string> preferredUids, PlanExclusions? exclusions = null)
    {
        var preferred = new HashSet<string>(preferredUids, StringComparer.Ordinal);
        var leaves = new List<LeafSource>();

        // Owned Pals grouped by species, gender and the start / preferred flags.
        foreach (var group in scoped.SelectMany(p => GendersOf(p).Select(g => (Pal: p, Gender: g)))
                     .GroupBy(x => (Species: x.Pal.SpeciesId.ToUpperInvariant(), x.Gender,
                         Start: start?.OwnedUid != null && x.Pal.Uid == start.OwnedUid,
                         Preferred: preferred.Contains(x.Pal.Uid))))
        {
            // The best Pal of the group first: the higher IV total, then the higher level (a plan uses the Pals it lists first).
            var pals = group.Select(x => x.Pal).BestFirst().ToList();
            leaves.Add(new LeafSource(pals[0].SpeciesId, group.Key.Gender, group.Key.Start, group.Key.Preferred, 0, pals, null));
        }

        // Wild leaves: every species obtainable without breeding, both genders.
        if (allowWild)
        {
            foreach (var pal in data.Pals)
            {
                if (!data.IsWildCatchable(pal.Id)) continue;
                if (excludeLegendary && pal.Rarity >= LegendaryRarity) continue;
                if (exclusions?.ExcludesSpecies(pal.Id) == true) continue;
                foreach (var gender in new[] { Gender.Male, Gender.Female })
                    leaves.Add(new LeafSource(pal.Id, gender, false, false, 1, [], ObtainMethodOf(pal) ?? "Wild"));
            }
        }

        // A start species is available in both genders, owned or not.
        if (start?.SpeciesId != null && data.TryNormalizePalId(start.SpeciesId, out var startId))
        {
            var genders = start.Gender is { } g ? new[] { g } : new[] { Gender.Male, Gender.Female };
            foreach (var gender in genders)
            {
                var owned = scoped.Where(p => string.Equals(p.SpeciesId, startId, StringComparison.OrdinalIgnoreCase) &&
                                              GendersOf(p).Contains(gender)).BestFirst().ToList();
                leaves.Add(owned.Count > 0
                    ? new LeafSource(startId, gender, true, false, 0, owned, null)
                    : new LeafSource(startId, gender, true, false, 1, [], ObtainMethodOf(data.Pal(startId)) ?? "Wild"));
            }
        }

        return leaves;
    }

    // A Pal of unknown gender can be used as either.
    private static IEnumerable<Gender> GendersOf(OwnedPal pal) =>
        pal.Gender is { } g ? [g] : [Gender.Male, Gender.Female];
}
