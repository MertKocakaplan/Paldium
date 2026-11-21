using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Planning;

/// <summary>Explains why no plan was found (shared by the species path finder and the passive solver).</summary>
public static class PlanDiagnostics
{
    /// <summary>
    /// Keys "key|argument": <see cref="PlanNotes.PassiveMissing"/> (wanted passive no Pal of the scope has),
    /// <see cref="PlanNotes.TargetSameSpeciesOnly"/> (the target is only bred from two of its own kind and fewer than two can be had)
    /// and <see cref="PlanNotes.LegendaryExcluded"/> (a legendary target Pal could only be caught, and catching legendaries is switched off).
    /// </summary>
    public static IEnumerable<string> Explain(
        IGameData data, IReadOnlyList<OwnedPal> scoped, string targetId, IReadOnlyList<string> desiredPassives,
        bool allowWild, bool excludeLegendary, PlanExclusions? exclusions = null)
    {
        var target = data.Pal(targetId);

        foreach (var passive in desiredPassives)
        {
            if (scoped.Any(p => p.Passives.Contains(passive, StringComparer.OrdinalIgnoreCase))) continue;
            // A wild species that carries it from birth (guaranteed passive) can bring it, when catching is allowed (T113).
            var wildHas = allowWild && data.Pals.Any(p => p.GuaranteedPassives.Contains(passive, StringComparer.OrdinalIgnoreCase) &&
                                                         data.IsWildCatchable(p.Id) && !(excludeLegendary && p.Rarity >= LeafBuilder.LegendaryRarity) &&
                                                         !(exclusions?.ExcludesSpecies(p.Id) == true));
            if (!wildHas) yield return $"{PlanNotes.PassiveMissing}|{passive}";
        }

        var ownedOfTarget = scoped.Count(p => string.Equals(p.SpeciesId, targetId, StringComparison.OrdinalIgnoreCase));
        var legendary = target.Rarity >= LeafBuilder.LegendaryRarity;
        var wildAllowed = allowWild && data.IsWildCatchable(targetId) && !(excludeLegendary && legendary);
        var obtainable = ownedOfTarget + (wildAllowed ? 2 : 0);

        // Nothing breeds it: only owning it or catching it helps.
        if (target.BreedingFlags.Unbreedable && ownedOfTarget == 0)
            yield return $"{PlanNotes.TargetUnbreedable}|{targetId}";

        if (target.BreedingFlags.SameSpeciesOnly && obtainable < 2)
            yield return $"{PlanNotes.TargetSameSpeciesOnly}|{targetId}";

        if (allowWild && excludeLegendary && legendary && data.IsWildCatchable(targetId) && ownedOfTarget < 2)
            yield return $"{PlanNotes.LegendaryExcluded}|{targetId}";
    }
}
