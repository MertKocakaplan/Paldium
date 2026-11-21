using Paldium.Core.Data;

namespace Paldium.Core.Probability;

public interface IPassiveOddsCalculator
{
    PassiveOddsResult Compute(PassiveOddsQuery query);

    /// <summary>The probability of one pool and step (k wanted and u unwanted in the pool, h of them targeted, w unwanted allowed).</summary>
    double Probability(int k, int u, int h, int w, int specialCount = 0);

    /// <summary>The six classes an egg of such a step falls into (T103).</summary>
    OutcomeDistribution Outcomes(int k, int u, int h, int w, double genderRight, int specialCount = 0);
}

/// <summary>
/// Inheritance odds of passives for one pair of parents (ARCHITECTURE 7.2). Passive ids are opaque strings here.
/// </summary>
/// <param name="CakeId">Cake id ("Cake05" = Special Cake) or null for no cake.</param>
/// <param name="UnknownUnwantedA">Parent A passives that are unknown; each counts as a distinct unwanted one.</param>
public sealed record PassiveOddsQuery(
    IReadOnlyList<string> ParentAPassives, IReadOnlyList<string> ParentBPassives,
    IReadOnlyList<string> Desired, int MaxUnwanted, string? CakeId = null,
    int UnknownUnwantedA = 0, int UnknownUnwantedB = 0);

/// <param name="ProbabilityPerEgg">Chance that one egg satisfies the query.</param>
/// <param name="ExpectedEggs">1 / probability, <see cref="double.PositiveInfinity"/> when impossible.</param>
/// <param name="InheritCountDistribution">Number of passives taken from the parents' pool and its probability.</param>
/// <param name="Warnings">Stable localization keys, see <see cref="PassiveOddsWarnings"/>.</param>
public sealed record PassiveOddsResult(
    double ProbabilityPerEgg, double ExpectedEggs,
    IReadOnlyList<(int inherited, double p)> InheritCountDistribution, IReadOnlyList<string> Warnings);

public static class PassiveOddsWarnings
{
    public const string DesiredMissing = "Odds.Warn.DesiredMissing";
    public const string PoolTooLarge = "Odds.Warn.PoolTooLarge";
    public const string RandomAddFailure = "Odds.Warn.RandomAddFailure";
}

/// <summary>
/// The six classes one egg of a step falls into (planner.md, "every egg"); they add up to 1. <c>Wanted</c>: right gender, every target passive,
/// no extra wanted one; <c>ExtraWanted</c>: right gender and another wanted passive too; <c>OtherGenderExtra</c> / <c>OtherGender</c>: the same with the
/// other gender; <c>ExtraUnwanted</c>: every target passive but too many unwanted ones (both genders); <c>Missing</c>: a target passive is missing.
/// </summary>
public sealed record OutcomeDistribution(double Wanted, double ExtraWanted, double OtherGenderExtra, double OtherGender, double ExtraUnwanted, double Missing)
{
    /// <summary>What the plan counts as success: classes 1 and 2.</summary>
    public double PlanSuccess => Wanted + ExtraWanted;

    public double Total => Wanted + ExtraWanted + OtherGenderExtra + OtherGender + ExtraUnwanted + Missing;
}

public sealed class PassiveOddsCalculator : IPassiveOddsCalculator
{
    private readonly double[] _inheritWeights; // index x = 1..max
    private readonly double[] _randomWeights;  // index r = 0..max-1
    private readonly IReadOnlyDictionary<string, int> _cakeOverride;

    public PassiveOddsCalculator(IGameData data)
    {
        var m = data.Mechanics;
        MaxPassives = m.MaxPassives;
        _inheritWeights = Normalize(m.PassiveInheritWeights, 1, MaxPassives);
        _randomWeights = Normalize(m.PassiveRandomAddWeights, 0, MaxPassives - 1);
        _cakeOverride = m.Cakes.ToDictionary(c => c.Id, c => c.PassiveInheritCountOverride, StringComparer.OrdinalIgnoreCase);
    }

    public int MaxPassives { get; }

    private static double[] Normalize(IReadOnlyDictionary<int, int> weights, int min, int max)
    {
        var result = new double[max + 1];
        double sum = 0;
        for (var i = min; i <= max; i++)
        {
            result[i] = weights.TryGetValue(i, out var w) ? w : 0;
            sum += result[i];
        }

        if (sum <= 0) throw new InvalidDataException("Passive weights are all zero.");
        for (var i = min; i <= max; i++) result[i] /= sum;
        return result;
    }

    /// <summary>
    /// Pure core of the formula: chance that an egg gets all <paramref name="h"/> wanted passives of the pool and at most
    /// <paramref name="w"/> unwanted ones. k = wanted passives in the pool, u = unwanted ones in the pool,
    /// <paramref name="specialCount"/> > 0 = Special Cake style (always takes that many from the pool, the rest is random).
    /// </summary>
    public double Probability(int k, int u, int h, int w, int specialCount = 0)
    {
        if (k < 0 || u < 0 || h < 0 || h > k || w < 0) return 0;
        var n = k + u;
        if (specialCount > 0)
        {
            var xe = Math.Min(specialCount, n);
            return Pool(k, u, h, w, xe, slots => (j, allowed) => slots <= allowed ? 1.0 : 0.0, MaxPassives - xe);
        }

        double total = 0;
        for (var x = 1; x <= MaxPassives; x++)
        {
            var xe = Math.Min(x, n);
            total += _inheritWeights[x] * Pool(k, u, h, w, xe, RandomAddOk, MaxPassives - xe);
        }

        return total;
    }

    // Chance that min(R, slots) <= allowed for the random additions.
    private Func<int, int, double> RandomAddOk(int slots) => (_, allowed) =>
    {
        if (allowed < 0) return 0;
        double p = 0;
        for (var r = 0; r < _randomWeights.Length; r++)
            if (Math.Min(r, slots) <= allowed) p += _randomWeights[r];
        return p;
    };

    private static double Pool(int k, int u, int h, int w, int xe, Func<int, Func<int, int, double>> random, int slots)
    {
        if (xe < h) return 0;
        var denominator = Combinatorics.Choose(k + u, xe);
        if (denominator == 0) return 0;
        var ok = random(slots);
        double sum = 0;
        for (var j = 0; j <= Math.Min(Math.Min(u, w), xe - h); j++)
        {
            var ways = Combinatorics.Choose(u, j) * Combinatorics.Choose(k - h, xe - h - j) / denominator;
            if (ways > 0) sum += ways * ok(j, w - j);
        }

        return sum;
    }

    /// <summary>
    /// Where one egg of a step lands (T103, "every egg"): the six classes of <see cref="OutcomeDistribution"/>, from the same model as
    /// <see cref="Probability"/>. k = wanted passives in the pool, u = unwanted ones, h = size of the step's target set (h of the k), w = unwanted
    /// allowed, <paramref name="genderRight"/> = chance that the child has the wanted gender (1 when the gender does not matter).
    /// </summary>
    public OutcomeDistribution Outcomes(int k, int u, int h, int w, double genderRight, int specialCount = 0)
    {
        var n = k + u;
        double okPlain = 0, okExtra = 0, over = 0;
        for (var x = 1; x <= MaxPassives; x++)
        {
            var weight = specialCount > 0 ? (x == specialCount ? 1.0 : 0.0) : _inheritWeights[x];
            if (weight <= 0) continue;
            var xe = Math.Min(x, n);
            var denominator = Combinatorics.Choose(n, xe);
            if (denominator == 0 || xe < h) continue;
            var slots = MaxPassives - xe;
            for (var j = 0; j <= Math.Min(u, xe - h); j++)
            {
                var b = xe - h - j;
                var ways = Combinatorics.Choose(u, j) * Combinatorics.Choose(k - h, b) / denominator;
                if (ways <= 0) continue;
                double accept = 0;
                if (specialCount > 0) accept = slots + j <= w ? 1.0 : 0.0;
                else
                    for (var r = 0; r < _randomWeights.Length; r++)
                        if (j + Math.Min(r, slots) <= w) accept += _randomWeights[r];
                if (b == 0) okPlain += weight * ways * accept; else okExtra += weight * ways * accept;
                over += weight * ways * (1 - accept);
            }
        }

        var all = okPlain + okExtra + over;
        return new OutcomeDistribution(
            Wanted: genderRight * okPlain, ExtraWanted: genderRight * okExtra,
            OtherGenderExtra: (1 - genderRight) * okExtra, OtherGender: (1 - genderRight) * okPlain,
            ExtraUnwanted: over, Missing: Math.Max(0, 1 - all));
    }

    public PassiveOddsResult Compute(PassiveOddsQuery q)
    {
        var pool = q.ParentAPassives.Concat(q.ParentBPassives).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var desired = q.Desired.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var k = desired.Count(d => pool.Contains(d, StringComparer.OrdinalIgnoreCase));
        var u = pool.Count - k + Math.Max(0, q.UnknownUnwantedA) + Math.Max(0, q.UnknownUnwantedB);
        var n = k + u;
        var special = q.CakeId != null && _cakeOverride.TryGetValue(q.CakeId, out var o) ? o : 0;

        var warnings = new List<string>();
        var missing = k < desired.Count;
        if (missing) warnings.Add(PassiveOddsWarnings.DesiredMissing);
        if (n > MaxPassives) warnings.Add(PassiveOddsWarnings.PoolTooLarge);
        warnings.Add(PassiveOddsWarnings.RandomAddFailure);

        var distribution = new SortedDictionary<int, double>();
        if (special > 0) distribution[Math.Min(special, n)] = 1;
        else
            for (var x = 1; x <= MaxPassives; x++)
                distribution[Math.Min(x, n)] = distribution.GetValueOrDefault(Math.Min(x, n)) + _inheritWeights[x];

        var p = missing ? 0 : Probability(k, u, desired.Count, Math.Max(0, q.MaxUnwanted), special);
        return new PassiveOddsResult(p, p > 0 ? 1 / p : double.PositiveInfinity,
            distribution.Select(kv => (kv.Key, kv.Value)).ToList(), warnings);
    }
}
