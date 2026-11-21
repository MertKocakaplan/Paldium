using System.Numerics;
using Paldium.Core.Data;
using Paldium.Core.Probability;

namespace Paldium.App.Pages.CalculatorTabs;

public sealed record PassiveOddsTerm(
    int InheritedCount,
    int PoolSize,
    int DesiredCount,
    int UnwantedInherited,
    int RandomSlots,
    int AllowedRandomUnwanted,
    bool IsSpecialCake,
    bool IsPoolCap,
    double InheritProbability,
    long WaysNumerator,
    long WaysDenominator,
    double RandomProbability,
    double TermProbability)
{
    public string WaysFraction
    {
        get
        {
            if (WaysDenominator <= 0) return "0";
            if (WaysNumerator <= 0) return "0";
            var gcd = (long)BigInteger.GreatestCommonDivisor(WaysNumerator, WaysDenominator);
            var n = WaysNumerator / gcd;
            var d = WaysDenominator / gcd;
            return d == 1 ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{n}/{d}";
        }
    }
}

public sealed record PassiveOddsBreakdown(
    int PoolSize,
    int DesiredInPoolCount,
    int DesiredTotalCount,
    int UnwantedInPoolCount,
    int UnknownUnwantedCount,
    IReadOnlyList<string> PoolIds,
    IReadOnlyList<string> UnwantedIds,
    IReadOnlyList<string> MissingDesiredIds,
    IReadOnlyList<PassiveOddsTerm> Terms,
    PassiveOddsTerm? DominantTerm,
    bool HasMultipleTerms,
    double TotalProbability);

public static class PassiveOddsFormula
{
    public static PassiveOddsBreakdown Evaluate(
        IGameData data,
        IReadOnlyList<string> parentA,
        IReadOnlyList<string> parentB,
        IReadOnlyList<string> desired,
        int maxUnwanted,
        string? cakeId = null,
        int unknownA = 0,
        int unknownB = 0)
    {
        var m = data.Mechanics;
        var maxPassives = m.MaxPassives;
        var inheritWeights = Normalize(m.PassiveInheritWeights, 1, maxPassives);
        var randomWeights = Normalize(m.PassiveRandomAddWeights, 0, maxPassives - 1);

        var pool = parentA.Concat(parentB).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var desiredList = desired.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var poolSet = pool.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desiredSet = desiredList.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = desiredList.Where(d => !poolSet.Contains(d)).ToList();
        var unwantedIds = pool.Where(p => !desiredSet.Contains(p)).ToList();
        var k = desiredList.Count - missing.Count;
        var h = desiredList.Count;
        var unknown = Math.Max(0, unknownA) + Math.Max(0, unknownB);
        var u = unwantedIds.Count + unknown;
        var n = k + u;
        var w = Math.Max(0, maxUnwanted);

        var special = 0;
        if (!string.IsNullOrEmpty(cakeId))
        {
            special = m.Cakes
                .FirstOrDefault(c => string.Equals(c.Id, cakeId, StringComparison.OrdinalIgnoreCase))
                ?.PassiveInheritCountOverride ?? 0;
        }

        var terms = new List<PassiveOddsTerm>();
        if (k == h)
        {
            if (special > 0)
            {
                var xe = Math.Min(special, n);
                AddTermsForInheritedCount(xe, isSpecial: true, special > n, 1.0, k, u, h, w, maxPassives, randomWeights, terms);
            }
            else
            {
                var grouped = new SortedDictionary<int, (double Weight, bool Multi)>();
                for (var x = 1; x <= maxPassives; x++)
                {
                    var xe = Math.Min(x, n);
                    if (grouped.TryGetValue(xe, out var cur))
                        grouped[xe] = (cur.Weight + inheritWeights[x], true);
                    else
                        grouped[xe] = (inheritWeights[x], false);
                }

                foreach (var (xe, (weight, multi)) in grouped)
                {
                    AddTermsForInheritedCount(xe, isSpecial: false, multi, weight, k, u, h, w, maxPassives, randomWeights, terms);
                }
            }
        }

        var positive = terms.Where(t => t.TermProbability > 0).ToList();
        var dominant = positive
            .OrderByDescending(t => t.TermProbability)
            .ThenBy(t => t.InheritedCount)
            .ThenBy(t => t.UnwantedInherited)
            .FirstOrDefault();

        return new PassiveOddsBreakdown(
            n,
            k,
            h,
            u,
            unknown,
            pool,
            unwantedIds,
            missing,
            positive,
            dominant,
            positive.Count > 1,
            positive.Sum(t => t.TermProbability));
    }

    private static void AddTermsForInheritedCount(
        int xe,
        bool isSpecial,
        bool isPoolCap,
        double inheritProb,
        int k,
        int u,
        int h,
        int w,
        int maxPassives,
        double[] randomWeights,
        List<PassiveOddsTerm> output)
    {
        var n = k + u;
        if (xe < h) return;
        var denom = (long)Math.Round(Combinatorics.Choose(n, xe));
        if (denom <= 0) return;

        var slots = maxPassives - xe;
        for (var j = 0; j <= Math.Min(Math.Min(u, w), xe - h); j++)
        {
            var numer = (long)Math.Round(Combinatorics.Choose(u, j) * Combinatorics.Choose(k - h, xe - h - j));
            if (numer <= 0) continue;

            var allowedRandom = w - j;
            var pRandom = isSpecial
                ? (slots <= allowedRandom ? 1.0 : 0.0)
                : RandomProbability(randomWeights, slots, allowedRandom);

            var prob = inheritProb * ((double)numer / denom) * pRandom;
            output.Add(new PassiveOddsTerm(
                xe,
                n,
                h,
                j,
                slots,
                allowedRandom,
                isSpecial,
                isPoolCap,
                inheritProb,
                numer,
                denom,
                pRandom,
                prob));
        }
    }

    private static double RandomProbability(double[] weights, int slots, int allowed)
    {
        if (allowed < 0) return 0;
        double p = 0;
        for (var r = 0; r < weights.Length; r++)
        {
            if (Math.Min(r, slots) <= allowed) p += weights[r];
        }
        return p;
    }

    private static double[] Normalize(IReadOnlyDictionary<int, int> weights, int min, int max)
    {
        var result = new double[max + 1];
        double sum = 0;
        for (var i = min; i <= max; i++)
        {
            result[i] = weights.TryGetValue(i, out var w) ? w : 0;
            sum += result[i];
        }

        if (sum <= 0) return result;
        for (var i = min; i <= max; i++) result[i] /= sum;
        return result;
    }
}
