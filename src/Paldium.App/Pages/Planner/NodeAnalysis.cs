using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Planning;
using Paldium.Core.Probability;

namespace Paldium.App.Pages.PlannerParts;

/// <summary>
/// The numbers behind the node panel (T103, planner.md "Node panel"): what an egg of a step becomes, what a wild catch brings, and the
/// "keep this Pal or try again" comparison. Pure functions over the plan, so they are tested without the page.
/// </summary>
public static class NodeAnalysis
{
    /// <summary>Wanted (k) and unwanted (u) passives of the pool of a step's two parents (ARCHITECTURE 7.2).</summary>
    public static (int Wanted, int Unwanted) PoolOf(PlanNode a, PlanNode b, IReadOnlyCollection<string> desired, IGameData? data = null)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unwantedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unwantedCount = 0;
        foreach (var node in new[] { a, b })
        {
            switch (node)
            {
                case OwnedLeaf owned:
                    foreach (var passive in owned.Pal.Passives)
                    {
                        if (desired.Contains(passive, StringComparer.OrdinalIgnoreCase)) wanted.Add(passive);
                        else unwantedNames.Add(passive);
                    }
                    break;
                case BreedStep step:
                    foreach (var passive in step.TargetPassives) wanted.Add(passive);
                    unwantedCount += step.MaxUnwanted;
                    break;
                case WildLeaf wild:
                    // The guaranteed passives of the species (T113): the wanted ones are held; the unwanted count already includes the others.
                    if (data != null && data.TryNormalizePalId(wild.SpeciesId, out var gid))
                        foreach (var passive in data.Pal(gid).GuaranteedPassives)
                            if (desired.Contains(passive, StringComparer.OrdinalIgnoreCase)) wanted.Add(passive);
                    unwantedCount += Math.Max(0, wild.AssumedUnwantedPassives);
                    break;
            }
        }

        return (wanted.Count, unwantedNames.Count + unwantedCount);
    }

    /// <summary>
    /// What one egg of <paramref name="step"/> becomes. <paramref name="flexible"/>: the child's gender does not matter (a backup partner exists),
    /// so classes 3 and 4 are empty. <paramref name="genderRight"/> is the chance of the wanted gender (1 when there is no wish).
    /// </summary>
    public static OutcomeDistribution StepOutcomes(IPassiveOddsCalculator odds, BreedStep step, IReadOnlyCollection<string> desired, double genderRight, bool flexible, IGameData? data = null)
    {
        var (k, u) = PoolOf(step.ParentA, step.ParentB, desired, data);
        var special = step.CakeId != null ? 4 : 0;
        return odds.Outcomes(k, u, step.TargetPassives.Count, step.MaxUnwanted, flexible ? 1.0 : genderRight, special);
    }

    /// <summary>The chance that the child has the step's wanted gender: 1 when the step does not care (or is flexible).</summary>
    public static double GenderRight(IBreedingCalculator breeding, BreedStep step) => step.ChildGender switch
    {
        GenderReq.Male => breeding.ChildGenderProbability(step.ChildSpeciesId, Gender.Male),
        GenderReq.Female => breeding.ChildGenderProbability(step.ChildSpeciesId, Gender.Female),
        _ => 1.0,
    };

    /// <summary>The first step of the plan that uses <paramref name="node"/> as a parent, with its 1-based number; null for the target.</summary>
    public static (BreedStep Step, int Number)? ConsumerOf(BreedingPlan plan, PlanNode node)
    {
        var steps = PlanText.StepsOf(plan);
        for (var i = 0; i < steps.Count; i++)
            if (ReferenceEquals(steps[i].ParentA, node) || ReferenceEquals(steps[i].ParentB, node)) return (steps[i], i + 1);
        return null;
    }

    /// <summary>
    /// What one egg of a step with a backup partner (T105) becomes, from the same model as the step's egg number: the flexible parent meets the
    /// main partner or the backup depending on its gender, so the two pools are weighted by that gender (T112). <paramref name="hatched"/> is the
    /// gender that hatched when the user already said which (then only that branch counts).
    /// </summary>
    public static OutcomeDistribution StepOutcomesWithBackup(
        IPassiveOddsCalculator odds, IBreedingCalculator breeding, BreedStep step, IReadOnlyCollection<string> desired, double genderRight, Gender? hatched, IGameData? data = null)
    {
        var flexible = step.ParentA is BreedStep { ChildGender: GenderReq.Any } ? step.ParentA : step.ParentB;
        var partner = ReferenceEquals(flexible, step.ParentA) ? step.ParentB : step.ParentA;
        var alt = step.AltPartner!;
        // The flexible Pal pairs with the main partner when it has the gender the partner needs.
        var mainGender = partner.Gender == GenderReq.Male ? Gender.Female : Gender.Male;
        var wMain = breeding.ChildGenderProbability(flexible.SpeciesId, mainGender);
        if (hatched is { } h) wMain = h == mainGender ? 1.0 : 0.0;
        var special = step.CakeId != null ? 4 : 0;
        OutcomeDistribution Of(PlanNode other)
        {
            var (k, u) = PoolOf(flexible, other, desired, data);
            return odds.Outcomes(k, u, step.TargetPassives.Count, step.MaxUnwanted, genderRight, special);
        }

        var main = wMain > 0 ? Of(partner) : null;
        var backup = wMain < 1 ? Of(alt) : null;
        double Mix(Func<OutcomeDistribution, double> pick) => (main == null ? 0 : wMain * pick(main)) + (backup == null ? 0 : (1 - wMain) * pick(backup));
        return new OutcomeDistribution(Mix(d => d.Wanted), Mix(d => d.ExtraWanted), Mix(d => d.OtherGenderExtra), Mix(d => d.OtherGender), Mix(d => d.ExtraUnwanted), Mix(d => d.Missing));
    }

    private sealed record Eval(int Have, IReadOnlyList<string>? Known, int Unwanted, double Eggs);

    /// <summary>
    /// "On the same path instead" (T114, T112): the plan tree stays as it is; <paramref name="candidate"/> takes the place of the owned Pal
    /// <paramref name="replaceUid"/> in every leaf that Pal is used in, and the probabilities and eggs of every step are worked out again with
    /// ARCHITECTURE 7.2 (the passives each step wants stay; the pool of a step is what its parents hold now). No solver is needed. Infinity when a
    /// step becomes impossible.
    /// </summary>
    public static double EggsWithReplacement(
        BreedingPlan plan, string replaceUid, OwnedPal candidate, IReadOnlyCollection<string> desired,
        IPassiveOddsCalculator odds, IBreedingCalculator breeding, IGameData? data = null)
    {
        var wanted = desired.Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        int MaskOf(IEnumerable<string> ids) =>
            Enumerable.Range(0, wanted.Count).Where(i => ids.Contains(wanted[i], StringComparer.OrdinalIgnoreCase)).Aggregate(0, (m, i) => m | (1 << i));

        Eval Go(PlanNode node)
        {
            switch (node)
            {
                case OwnedLeaf o:
                    var pal = o.Pal.Uid == replaceUid ? candidate : o.Pal;
                    var known = pal.Passives.Where(p => !wanted.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    return new Eval(MaskOf(pal.Passives), known, known.Count, 0);
                case WildLeaf w:
                    var guaranteed = data != null && data.TryNormalizePalId(w.SpeciesId, out var wid) ? data.Pal(wid).GuaranteedPassives : [];
                    return new Eval(MaskOf(guaranteed), null, Math.Max(0, w.AssumedUnwantedPassives), 0);
                case BreedStep s:
                    var a = Go(s.ParentA);
                    var b = Go(s.ParentB);
                    var h = System.Numerics.BitOperations.PopCount((uint)MaskOf(s.TargetPassives));
                    var special = s.CakeId != null ? 4 : 0;
                    double P(Eval x, Eval y)
                    {
                        var k = System.Numerics.BitOperations.PopCount((uint)(x.Have | y.Have));
                        var u = x.Known != null && y.Known != null ? x.Known.Union(y.Known, StringComparer.OrdinalIgnoreCase).Count() : x.Unwanted + y.Unwanted;
                        return odds.Probability(k, u, h, s.MaxUnwanted, special);
                    }

                    var factor = s.ChildGender switch
                    {
                        GenderReq.Male => breeding.ChildGenderProbability(s.ChildSpeciesId, Gender.Male),
                        GenderReq.Female => breeding.ChildGenderProbability(s.ChildSpeciesId, Gender.Female),
                        _ => 1.0,
                    };
                    double stepEggs;
                    if (s.AltPartner == null)
                    {
                        var p = P(a, b);
                        stepEggs = p <= 0 || factor <= 0 ? double.PositiveInfinity : 1.0 / (p * factor);
                    }
                    else
                    {
                        // A backup partner (T105): the flexible Pal meets the main partner or the backup by its gender.
                        var flexIsA = s.ParentA is BreedStep { ChildGender: GenderReq.Any };
                        var flex = flexIsA ? a : b;
                        var partner = flexIsA ? b : a;
                        var partnerNode = flexIsA ? s.ParentB : s.ParentA;
                        var alt = Go(s.AltPartner);
                        var flexSpecies = (flexIsA ? s.ParentA : s.ParentB).SpeciesId;
                        var mainGender = partnerNode.Gender == GenderReq.Male ? Gender.Female : Gender.Male;
                        var wMain = breeding.ChildGenderProbability(flexSpecies, mainGender);
                        var pMain = P(flex, partner);
                        var pAlt = P(flex, alt);
                        stepEggs = pMain <= 0 || pAlt <= 0 || factor <= 0 ? double.PositiveInfinity : (wMain / pMain + (1 - wMain) / pAlt) / factor;
                    }

                    return new Eval(MaskOf(s.TargetPassives), null, s.MaxUnwanted, a.Eggs + b.Eggs + stepEggs);
                default:
                    return new Eval(0, null, 0, double.PositiveInfinity);
            }
        }

        return Go(plan.Root).Eggs;
    }

    /// <summary>
    /// The expected eggs left when the step is tried again: the step and every step after it in the order of the checklist, without the steps
    /// already done (<paramref name="done"/>: 0-based indexes of the checklist).
    /// </summary>
    public static double RemainingIfRetry(BreedingPlan plan, BreedStep step, IReadOnlySet<int>? done = null)
    {
        var steps = PlanText.StepsOf(plan);
        var from = -1;
        for (var i = 0; i < steps.Count; i++)
            if (ReferenceEquals(steps[i], step)) { from = i; break; }
        if (from < 0) return step.ExpectedEggs;
        double sum = 0;
        for (var i = from; i < steps.Count; i++)
            if (done == null || !done.Contains(i)) sum += steps[i].ExpectedEggs;
        return sum;
    }

    /// <summary>
    /// False when no passive is wanted (a plan of species only): the unwanted passives of a Pal change nothing, so the passive classes of
    /// "every egg" and "every catch" are not worked out (they would call a perfectly good step impossible).
    /// </summary>
    public static bool PassivesMatter(IReadOnlyCollection<string> desired) => desired.Count > 0;

    public sealed record CatchRow(string Kind, double Probability, double StepEggs);

    /// <summary>
    /// "Every catch" (wild leaf): how many passives a caught Pal has (weights by count) against the number the plan assumed, and the
    /// expected eggs of the step that uses it for that count. <paramref name="stepEggs"/> gives the eggs for a number of unwanted passives.
    /// </summary>
    public static IReadOnlyList<CatchRow> WildCatch(IReadOnlyList<int> weights, int assumed, Func<int, double> stepEggs, int guaranteedUnwanted = 0)
    {
        double total = weights.Sum();
        if (total <= 0) return [];
        var minUnwanted = Math.Max(0, guaranteedUnwanted);
        var effectiveAssumed = Math.Max(Math.Max(0, assumed), minUnwanted);
        int Effective(int c) => Math.Max(c, minUnwanted);
        double Sum(Func<int, bool> pick) => Enumerable.Range(0, weights.Count).Where(c => pick(Effective(c))).Sum(c => weights[c]) / total;
        var rows = new List<CatchRow>();
        var fewer = Sum(u => u < effectiveAssumed);
        if (fewer > 0) rows.Add(new CatchRow("fewer", fewer, stepEggs(minUnwanted)));
        rows.Add(new CatchRow("equal", Sum(u => u == effectiveAssumed), stepEggs(effectiveAssumed)));
        var more = Sum(u => u > effectiveAssumed);
        if (more > 0) rows.Add(new CatchRow("more", more, stepEggs(effectiveAssumed + 1)));
        return rows;
    }

    /// <summary>"Every catch" rows for a wild leaf and its consuming breeding step.</summary>
    public static IReadOnlyList<CatchRow> CatchRowsFor(
        WildLeaf wild, BreedStep step, IReadOnlyCollection<string> desired,
        IPassiveOddsCalculator odds, IBreedingCalculator breeding, IGameData data)
    {
        var (k, u) = PoolOf(step.ParentA, step.ParentB, desired, data);
        // The pool counted this leaf's unwanted passives (the assumed number, at least the guaranteed unwanted ones; T113); the rows change that number.
        var baseUnwanted = Math.Max(0, u - Math.Max(0, wild.AssumedUnwantedPassives));
        var guaranteedUnwanted = data.TryNormalizePalId(wild.SpeciesId, out var gid)
            ? data.Pal(gid).GuaranteedPassives.Count(p => !desired.Contains(p, StringComparer.OrdinalIgnoreCase))
            : 0;
        var genderRight = GenderRight(breeding, step);
        double EggsFor(int count)
        {
            var p = odds.Probability(k, baseUnwanted + Math.Max(count, guaranteedUnwanted), step.TargetPassives.Count, step.MaxUnwanted, step.CakeId != null ? 4 : 0) * genderRight;
            return p <= 0 ? double.PositiveInfinity : 1.0 / p;
        }

        return WildCatch(data.Mechanics.WildPassiveCountWeights, Math.Max(0, wild.AssumedUnwantedPassives), EggsFor, guaranteedUnwanted);
    }

    public enum VerdictKind { Exact, ExtraWanted, Unwanted, OtherGender, Missing }

    /// <summary>The verdict for a Pal in hand: what kind of Pal it is for the step, and whether continuing from it beats trying again.</summary>
    public sealed record Verdict(VerdictKind Kind, double? WithPal, double Retry, bool ContinueFromPal);

    /// <summary>
    /// Decision rule (planner.md): compare the eggs left with the Pal (<paramref name="withPal"/>, null when the solver finds no plan) against
    /// trying again (<paramref name="retry"/>); the smaller wins and a tie goes to the Pal.
    /// </summary>
    public static Verdict Decide(BreedStep step, IReadOnlyList<string> palPassives, Gender? palGender, IReadOnlyCollection<string> desired, double? withPal, double retry)
    {
        bool Has(string passive) => palPassives.Contains(passive, StringComparer.OrdinalIgnoreCase);
        var missing = step.TargetPassives.Any(p => !Has(p));
        var extra = palPassives.Any(p => desired.Contains(p, StringComparer.OrdinalIgnoreCase) && !step.TargetPassives.Contains(p, StringComparer.OrdinalIgnoreCase));
        var unwanted = palPassives.Count(p => !desired.Contains(p, StringComparer.OrdinalIgnoreCase));
        var wrongGender = palGender != null && ((step.ChildGender == GenderReq.Male && palGender == Gender.Female) || (step.ChildGender == GenderReq.Female && palGender == Gender.Male));

        var kind = missing ? VerdictKind.Missing
            : wrongGender ? VerdictKind.OtherGender
            : unwanted > step.MaxUnwanted ? VerdictKind.Unwanted
            : extra ? VerdictKind.ExtraWanted
            : VerdictKind.Exact;
        var continueFromPal = withPal is { } w && w <= retry + 1e-9;
        return new Verdict(kind, withPal, retry, continueFromPal);
    }
}
