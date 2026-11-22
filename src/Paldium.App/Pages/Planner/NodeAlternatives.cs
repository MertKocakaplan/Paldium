using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Planning;
using Paldium.App.Services;

namespace Paldium.App.Pages.PlannerParts;

/// <summary>What else could stand in the place of a node (T107, T114): species for a wild partner, and how often an owned Pal is used.</summary>
public static class NodeAlternatives
{
    public static IEnumerable<PlanNode> Walk(PlanNode n)
    {
        yield return n;
        if (n is BreedStep s)
        {
            foreach (var x in Walk(s.ParentA)) yield return x;
            foreach (var x in Walk(s.ParentB)) yield return x;
            if (s.AltPartner != null)
                foreach (var x in Walk(s.AltPartner)) yield return x;
        }
    }

    /// <summary>The step that has <paramref name="node"/> as a parent (reference equality), or null.</summary>
    public static BreedStep? StepOf(BreedingPlan plan, PlanNode node) =>
        Walk(plan.Root).OfType<BreedStep>().FirstOrDefault(s =>
            ReferenceEquals(s.ParentA, node) || ReferenceEquals(s.ParentB, node) || ReferenceEquals(s.AltPartner, node));

    /// <summary>
    /// Other wild-catchable species that make the same step (same child, the genders the step needs) with the same partner; the step needs the
    /// same eggs. Excluded species and, when the setting says so, legendary species are left out.
    /// </summary>
    public static IReadOnlyList<string> SameStepSpecies(IGameData data, IBreedingCalculator breeding, PlannerSettings query, BreedingPlan plan, WildLeaf wild)
    {
        if (StepOf(plan, wild) is not { } step) return [];
        var nodeIsA = ReferenceEquals(step.ParentA, wild);
        var partner = nodeIsA ? step.ParentB : step.ParentA;
        var excluded = PlanExclusions.Create(data, query.ExcludedUids, query.ExcludedSpecies, query.ExcludedGroups, null, plan.Root.SpeciesId);
        var list = new List<string>();
        foreach (var pal in data.Pals)
        {
            if (string.Equals(pal.Id, wild.SpeciesId, StringComparison.OrdinalIgnoreCase) || !data.IsWildCatchable(pal.Id)) continue;
            if (query.ExcludeLegendaryWild && pal.Rarity >= LeafBuilder.LegendaryRarity) continue;
            if (excluded.ExcludesSpecies(pal.Id)) continue;
            var outcomes = nodeIsA ? breeding.GetChildOutcomes(pal.Id, partner.SpeciesId) : breeding.GetChildOutcomes(partner.SpeciesId, pal.Id);
            if (outcomes.Any(o => string.Equals(o.ChildId, step.ChildSpeciesId, StringComparison.OrdinalIgnoreCase) &&
                                  Fits(nodeIsA ? o.AGender : o.BGender, wild.Gender) && Fits(nodeIsA ? o.BGender : o.AGender, partner.Gender)))
                list.Add(pal.Id);
        }

        return list;
    }

    private static bool Fits(GenderReq required, GenderReq actual) => required == GenderReq.Any || actual == GenderReq.Any || required == actual;

    /// <summary>The 1-based numbers of the steps (checklist order) in which the owned Pal is a parent.</summary>
    public static IReadOnlyList<int> StepsUsing(BreedingPlan plan, string uid)
    {
        var steps = PlanText.StepsOf(plan);
        var list = new List<int>();
        for (var i = 0; i < steps.Count; i++)
            if ((steps[i].ParentA is OwnedLeaf a && a.Pal.Uid == uid)
                || (steps[i].ParentB is OwnedLeaf b && b.Pal.Uid == uid)
                || (steps[i].AltPartner is OwnedLeaf alt && alt.Pal.Uid == uid))
                list.Add(i + 1);
        return list;
    }

    /// <summary>How many steps each owned Pal of the plan serves (by UID).</summary>
    public static IReadOnlyDictionary<string, int> UseCounts(BreedingPlan? plan)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (plan == null) return counts;
        foreach (var step in Walk(plan.Root).OfType<BreedStep>())
        {
            if (step.ParentA is OwnedLeaf oa) counts[oa.Pal.Uid] = counts.GetValueOrDefault(oa.Pal.Uid) + 1;
            if (step.ParentB is OwnedLeaf ob) counts[ob.Pal.Uid] = counts.GetValueOrDefault(ob.Pal.Uid) + 1;
            if (step.AltPartner is OwnedLeaf oalt) counts[oalt.Pal.Uid] = counts.GetValueOrDefault(oalt.Pal.Uid) + 1;
        }
        return counts;
    }

    /// <summary>"1", "1 and 2", "1, 2 and 3" (English) / "1.", "1. ve 2.", "1., 2. ve 3." (Turkish).</summary>
    public static string JoinNumbers(IReadOnlyList<int> numbers, string lang)
    {
        var parts = numbers.Select(n => lang == "tr" ? n + "." : n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (parts.Count <= 1) return string.Join("", parts);
        var and = lang == "tr" ? " ve " : " and ";
        return string.Join(", ", parts.Take(parts.Count - 1)) + and + parts[^1];
    }
}
