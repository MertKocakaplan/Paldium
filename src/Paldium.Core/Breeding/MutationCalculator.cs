using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Breeding;

/// <summary>One possible mutation result: the species and its chance among the mutation outcomes of the pair (they add up to 1).</summary>
/// <param name="HasAlphaForm">The game has an Alpha (rare) form of the species; a mutant is always born as an Alpha.</param>
public sealed record MutationOutcome(string SpeciesId, double Probability, bool HasAlphaForm);

public sealed record MutationPair(string A, string B, double Probability);

public interface IMutationCalculator
{
    /// <summary>Children a pair can have when a mutation happens (not the chance that it happens, see <see cref="MutationRate"/>).</summary>
    IReadOnlyList<MutationOutcome> MutationOutcomes(string a, string b);

    /// <summary>Chance that an egg mutates: the base rate plus the cake's bonus (Cake04: 0.03).</summary>
    double MutationRate(string? cakeId);

    /// <summary>Pairs whose mutation can give the target, most likely first (each pair once; the order of the parents is not significant).</summary>
    IReadOnlyList<MutationPair> PairsMutatingInto(string target, int maxResults);
}

/// <summary>
/// Mutation child species (ARCHITECTURE 7.5): with m = min(rankA, rankB) and d = |rankA - rankB| the target power is
/// <c>max(U{0..n-1} + Q(2m·randomCoefficient... ) ...)</c>, all in float32, and the nearest species is taken with the recipe
/// children included.
/// </summary>
public sealed class MutationCalculator : IMutationCalculator
{
    private readonly IGameData _data;
    private readonly BreedingRules _rules;
    private readonly Dictionary<string, MutationOutcome[]> _outcomeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _nearestCache = [];
    private readonly HashSet<string> _alphaForms;
    private Dictionary<string, List<MutationPair>>? _byTarget;
    private readonly object _gate = new();

    public MutationCalculator(IGameData data, BreedingRules rules)
    {
        _data = data;
        _rules = rules;
        _alphaForms = rules.Rows.Where(r => r.Id.StartsWith("BOSS_", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Id["BOSS_".Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Q(x): float32 value rounded half to even, then shifted right by one (integer division by two, rounding down).</summary>
    public static int Q(float x) => (int)MathF.Round(x, MidpointRounding.ToEven) >> 1;

    public double MutationRate(string? cakeId)
    {
        var bonus = cakeId == null ? 0 : _data.Mechanics.Cakes.FirstOrDefault(c => string.Equals(c.Id, cakeId, StringComparison.OrdinalIgnoreCase))?.MutationRateBonusPercent ?? 0;
        return Math.Round(_data.Mechanics.Mutation.Rate + bonus / 100.0, 10);
    }

    /// <summary>The smallest and largest target power the pair can aim for, and the number of equally likely values.</summary>
    public (int First, int Count) TargetRange(string a, string b)
    {
        var rankA = _rules.RankOf(a);
        var rankB = _rules.RankOf(b);
        var m = Math.Min(rankA, rankB);
        var d = Math.Abs(rankA - rankB);
        var mutation = _data.Mechanics.Mutation;
        var n = Math.Max(Q(2f * m * (float)mutation.RandomCoefficient), 1);
        var offset = Q(2f * m * (float)mutation.RankCoefficient) + Q(2f * d * (float)mutation.RankDiffPenalty) + 1;
        return (offset, n);
    }

    public IReadOnlyList<MutationOutcome> MutationOutcomes(string a, string b)
    {
        var key = string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
        lock (_gate)
        {
            if (_outcomeCache.TryGetValue(key, out var cached)) return cached;
            var (offset, n) = TargetRange(a, b);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var u = 0; u < n; u++)
            {
                var child = Nearest(Math.Max(u + offset, 1));
                counts[child] = counts.GetValueOrDefault(child) + 1;
            }

            var result = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new MutationOutcome(kv.Key, kv.Value / (double)n, _alphaForms.Contains(kv.Key))).ToArray();
            _outcomeCache[key] = result;
            return result;
        }
    }

    // Nearest species for a target power with the recipe children as candidates (mutation ignores the recipes).
    private string Nearest(int target)
    {
        if (!_nearestCache.TryGetValue(target, out var id))
            _nearestCache[target] = id = _rules.Nearest(target, excludeRecipeChildren: false);
        return id;
    }

    public IReadOnlyList<MutationPair> PairsMutatingInto(string target, int maxResults)
    {
        Dictionary<string, List<MutationPair>> index;
        lock (_gate) index = _byTarget ??= BuildIndex();
        return index.TryGetValue(target, out var pairs) ? pairs.Take(Math.Max(0, maxResults)).ToList() : [];
    }

    // Every unordered pair once; computed on the first call and kept.
    private Dictionary<string, List<MutationPair>> BuildIndex()
    {
        var ids = _data.Pals.Select(p => p.Id).Order(StringComparer.Ordinal).ToList();
        var index = new Dictionary<string, List<MutationPair>>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
            for (var j = i; j < ids.Count; j++)
            {
                var (offset, n) = TargetRange(ids[i], ids[j]);
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var u = 0; u < n; u++)
                {
                    var child = Nearest(Math.Max(u + offset, 1));
                    counts[child] = counts.GetValueOrDefault(child) + 1;
                }

                foreach (var (child, count) in counts)
                {
                    if (!index.TryGetValue(child, out var list)) index[child] = list = [];
                    list.Add(new MutationPair(ids[i], ids[j], count / (double)n));
                }
            }

        foreach (var list in index.Values)
            list.Sort((x, y) => y.Probability != x.Probability ? y.Probability.CompareTo(x.Probability)
                : string.CompareOrdinal(x.A + x.B, y.A + y.B));
        return index;
    }
}
