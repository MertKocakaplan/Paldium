using Paldium.Core.Domain;

namespace Paldium.Core.Planning;

/// <summary>
/// "Which preferred Pal is best?" (ARCHITECTURE 7.4): one solve per preferred Pal, in parallel, each allowed to use only that
/// Pal as the preferred one.
/// </summary>
internal static class FeasibilityRunner
{
    public const string NoWantedPassives = "Plan.Reason.NoWantedPassives";

    public static async Task<IReadOnlyList<PreferredFeasibility>> RunAsync(
        PassiveSolver solver, GoalQuery query, IReadOnlyList<OwnedPal> owned, CancellationToken ct, PlanResult? main = null)
    {
        var tasks = query.PreferredUids.Distinct(StringComparer.Ordinal).Select(uid => Task.Run(() =>
        {
            // The cheapest plan of the main result already uses this Pal: it is the best plan for it, nothing to solve (T110).
            if (main?.Plans.FirstOrDefault() is { } first && first.PreferredUidsUsed.Contains(uid, StringComparer.Ordinal))
                return new PreferredFeasibility(uid, first, null);

            // The fast stage only: no improving time per Pal, and the parallel runs leave the solver's statistics alone.
            var single = query with { PreferredUids = [uid], PreferredMode = PreferredMode.AtLeastOne, MaxResults = 1, TimeLimit = TimeSpan.Zero, Exhaustive = false };
            var result = solver.SolveFirstStage(single, owned, ct);
            if (result.Plans.Count > 0) return new PreferredFeasibility(uid, result.Plans[0], null);

            var pal = owned.FirstOrDefault(p => p.Uid == uid);
            var hasWanted = pal != null && query.DesiredPassives.Any(d => pal.Passives.Contains(d, StringComparer.OrdinalIgnoreCase));
            var reason = !hasWanted && query.DesiredPassives.Count > 0 ? NoWantedPassives
                : result.Diagnostics.Contains(PlanNotes.StepLimit) ? PlanNotes.StepsOnly
                : PlanNotes.NotUsable;
            return new PreferredFeasibility(uid, null, reason);
        }, ct)).ToList();

        return await Task.WhenAll(tasks);
    }
}
