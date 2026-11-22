using System.Diagnostics;
using System.Numerics;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Probability;

namespace Paldium.Core.Planning;

/// <summary>
/// Passive oriented solver (ARCHITECTURE 7.4): generalized Dijkstra over (species, gender, wanted passives held,
/// unwanted count, flags) states with cost expected eggs (then steps, then wild Pals).
/// </summary>
internal sealed record PairInfo((ChildOutcome Outcome, int Child, int Remaining)[] Outcomes, int MinRemaining)
{
    /// <summary>T115: every child bucket of the pair already holds a leaf (a pair of plain Pals can only make states that are dropped); true stays true.</summary>
    public volatile bool Saturated;

    /// <summary>The leaf count at which <see cref="Saturated"/> was last worked out as false.</summary>
    public volatile int CheckedAt = -1;
}

public sealed record SolverStats(long Generated, long Expanded, long Pairs, long Milliseconds);

public sealed class PassiveSolver : IPassiveSolver
{
    public const string SpecialCakeId = "Cake05";
    public const int DefaultStateLimit = 2_000_000;

    // Alternatives costing more than best * factor + margin expected eggs are not searched for.
    private const double AlternativeCostFactor = 8;
    private const double AlternativeCostMargin = 20;

    private readonly IGameData _data;
    private readonly IBreedingCalculator _breeding;
    private readonly PassiveOddsCalculator _odds;

    public PassiveSolver(IGameData data, IBreedingCalculator breeding, PassiveOddsCalculator odds)
    {
        _data = data;
        _breeding = breeding;
        _odds = odds;
    }

    /// <summary>Counters of the last search (generated states, expanded states, parent pairs tried, milliseconds).</summary>
    public SolverStats LastStats { get; private set; } = new(0, 0, 0, 0);

    /// <summary>
    /// Worker threads of the search (T119): the candidates of an expansion are made in parallel and merged in order, so the result does not
    /// depend on this number. Default: all logical processors but one (the window stays responsive), at least 1.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Pairs one worker thread should have at least in an expansion (fewer pairs: fewer workers; a worker costs a wake-up).</summary>
    internal static int MinPairsPerWorker { get; set; } = 16;

    /// <summary>Upper bound of generated states; the search stops with the best results found when it is reached.</summary>
    public int StateLimit { get; init; } = DefaultStateLimit;

    public async Task<PlanResult> SolveAsync(GoalQuery query, IReadOnlyList<OwnedPal> owned, CancellationToken ct, IProgress<SolverProgress>? progress = null)
    {
        var result = await Task.Run(() => Solve(query, owned, ct, progress), ct);
        if (query.PreferredUids.Count == 0) return result;
        var feasibility = await FeasibilityRunner.RunAsync(this, query, owned, ct, result);
        return result with { Feasibility = feasibility };
    }

    private sealed record RunOptions(bool Phase2, double UpperBound, Stopwatch Watch, TimeSpan? Limit, Action<IReadOnlyList<BreedingPlan>>? OnImprove, bool Record = true, int Threads = 1);

    /// <summary>The candidate states one part of an expansion made, in the order the sequential search would have pushed them.</summary>
    private sealed class PairChunk
    {
        public readonly List<SolverState> States = [];
        public long Combos;
    }

    /// <summary>
    /// The fast stage only, with no time limit and without touching <see cref="LastStats"/> (the "which preferred Pal" runs, which go in parallel; T110).
    /// </summary>
    internal PlanResult SolveFirstStage(GoalQuery q, IReadOnlyList<OwnedPal> owned, CancellationToken ct) =>
        Run(q, owned, ct, null, new RunOptions(false, double.PositiveInfinity, Stopwatch.StartNew(), null, null, Record: false));

    /// <summary>
    /// Two stages (ARCHITECTURE 7.4): a fast search with narrowed candidate sets, then an improving search over all subsets of the
    /// wanted passives, pruned with an admissible lower bound and the first stage's result, inside the remaining time limit
    /// (no limit with <see cref="GoalQuery.Exhaustive"/>; a zero <see cref="GoalQuery.TimeLimit"/> without it skips the improving stage). Improvements are reported through <paramref name="progress"/>.
    /// </summary>
    public PlanResult Solve(GoalQuery q, IReadOnlyList<OwnedPal> owned, CancellationToken ct, IProgress<SolverProgress>? progress = null)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan? limit = q.TimeLimit > TimeSpan.Zero ? q.TimeLimit : null; // first stage: no limit with zero
        // The time limit is the improving stage's time (T110): the fast stage has no limit (cancel and the state limit still stop it).
        var stage1 = Run(q, owned, ct, progress, new RunOptions(false, double.PositiveInfinity, watch, null, null, Threads: MaxDegreeOfParallelism));
        if (stage1.Plans.Count == 0 && !stage1.TimedOut) stage1 = WithBlockers(q, owned, stage1, ct);
        if (stage1.TimedOut || (stage1.Plans.Count > 0 && stage1.Plans[0].Steps == 0)) return stage1;
        // Improvement time "off" (zero and not exhaustive): the first stage is the answer, the improving stage never starts.
        if (q.TimeLimit <= TimeSpan.Zero && !q.Exhaustive) return stage1;
        progress?.Report(new SolverProgress("stage1", 0, 0, stage1.Plans.Count > 0 ? stage1.Plans[0].ExpectedEggsTotal : null, stage1));
        var firstStats = LastStats;
        return Improve(q, owned, stage1, ct, progress, Stopwatch.StartNew(), q.Exhaustive ? null : limit, firstStats);
    }

    /// <summary>
    /// No plan and the query has exclusions: each one (the last four added) is lifted on its own and the fast stage asked again; the ones that
    /// give a plan are the blockers (T107 diagnosis).
    /// </summary>
    private PlanResult WithBlockers(GoalQuery q, IReadOnlyList<OwnedPal> owned, PlanResult none, CancellationToken ct)
    {
        var all = PlanExclusions.ListOf(q.ExcludedUids, q.ExcludedSpecies, q.ExcludedGroups);
        if (all.Count == 0) return none;
        var blockers = new List<ExclusionBlocker>();
        foreach (var exclusion in all.Skip(Math.Max(0, all.Count - 4)))
        {
            ct.ThrowIfCancellationRequested();
            var without = q with
            {
                ExcludedUids = q.ExcludedUids?.Where(u => !(exclusion.Kind == PlanExclusion.UidKind && u == exclusion.Id)).ToList(),
                ExcludedSpecies = q.ExcludedSpecies?.Where(s => !(exclusion.Kind == PlanExclusion.SpeciesKind && s == exclusion.Id)).ToList(),
                ExcludedGroups = q.ExcludedGroups?.Where(g => !(exclusion.Kind == PlanExclusion.GroupKind && g == exclusion.Id)).ToList(),
                MaxResults = 1,
            };
            var result = Run(without, owned, ct, null, new RunOptions(false, double.PositiveInfinity, Stopwatch.StartNew(), null, null, Record: false, Threads: MaxDegreeOfParallelism));
            if (result.Plans.Count > 0) blockers.Add(new ExclusionBlocker(exclusion, result.Plans[0].ExpectedEggsTotal, result.Plans[0].Steps));
        }

        return none with { Blockers = blockers };
    }

    /// <summary>
    /// "Keep searching" after the improving stage ran out of time: only the improving stage runs again, with the plans found so far as the
    /// upper bound (so it never returns a worse plan) and the same time limit (30 s when improvement is off; none when exhaustive). The
    /// plans of <paramref name="previous"/> stay in the result.
    /// </summary>
    public PlanResult Continue(GoalQuery q, IReadOnlyList<OwnedPal> owned, PlanResult previous, CancellationToken ct, IProgress<SolverProgress>? progress = null)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan? limit = q.Exhaustive ? null : q.TimeLimit > TimeSpan.Zero ? q.TimeLimit : TimeSpan.FromSeconds(ContinueSecondsWhenOff);
        var basis = previous with { Diagnostics = previous.Diagnostics.Where(d => d != PlanNotes.TimedOut && d != PlanNotes.BetterMayExist).ToList(), TimedOut = false };
        return Improve(q, owned, basis, ct, progress, watch, limit, new SolverStats(0, 0, 0, 0));
    }

    public Task<PlanResult> ContinueAsync(GoalQuery q, IReadOnlyList<OwnedPal> owned, PlanResult previous, CancellationToken ct, IProgress<SolverProgress>? progress = null) =>
        Task.Run(() => Continue(q, owned, previous, ct, progress), ct);

    /// <summary>The time a "keep searching" run gets when the improvement time is off.</summary>
    public const int ContinueSecondsWhenOff = 30;

    // The improving stage: all subsets of the wanted passives, bounded by the plans of <paramref name="stage1"/> (the first stage or an earlier run).
    private PlanResult Improve(GoalQuery q, IReadOnlyList<OwnedPal> owned, PlanResult stage1, CancellationToken ct, IProgress<SolverProgress>? progress,
        Stopwatch watch, TimeSpan? limit2, SolverStats firstStats)
    {
        var maxResults = Math.Max(1, q.MaxResults);
        var upper = stage1.Plans.Count == 0 ? double.PositiveInfinity
            : stage1.Plans.Count >= maxResults ? stage1.Plans[maxResults - 1].ExpectedEggsTotal
            : stage1.Plans[0].ExpectedEggsTotal * 1.5 + 2; // fewer alternatives than asked: look for clearly better or similar ones

        PlanResult Merge(IReadOnlyList<BreedingPlan> improved, bool timedOut)
        {
            var all = stage1.Plans.Concat(improved)
                .GroupBy(p => SpeciesPathFinder.PathKeyOf(p.Root), StringComparer.Ordinal).Select(g => g.OrderBy(p => p.ExpectedEggsTotal).ThenBy(p => p.Steps).First())
                .OrderBy(p => p.ExpectedEggsTotal).ThenBy(p => p.Steps).Take(maxResults).ToList();
            var diagnostics = stage1.Diagnostics.Where(d => d != PlanNotes.NoPlan && !d.StartsWith("Plan.Diag.", StringComparison.Ordinal) || all.Count == 0).ToList();
            if (timedOut) { diagnostics.Add(PlanNotes.TimedOut); diagnostics.Add(PlanNotes.BetterMayExist); }
            return new PlanResult(all, stage1.Feasibility, diagnostics.Distinct().ToList(), timedOut, stage1.OwnedTargets, stage1.OwnedMatching,
                all.Count == 0 ? stage1.Blockers : null);
        }

        var stage2 = Run(q, owned, ct, progress, new RunOptions(true, upper, watch, limit2, improved =>
            progress?.Report(new SolverProgress("improve", 0, 0, improved.Count > 0 ? improved.Min(p => p.ExpectedEggsTotal) : null, Merge(improved, false))), Threads: MaxDegreeOfParallelism));
        var merged = Merge(stage2.Plans, stage2.TimedOut);
        // Plans found by the improving stage replace the diagnostics of an empty first stage.
        if (merged.Plans.Count == 0) merged = merged with { Diagnostics = stage2.Diagnostics };
        LastStats = new SolverStats(firstStats.Generated + LastStats.Generated, firstStats.Expanded + LastStats.Expanded,
            firstStats.Pairs + LastStats.Pairs, watch.ElapsedMilliseconds);
        return merged;
    }

    /// <summary>Stage 1 candidate sets of a child: the whole pool, each parent's own set, every set that contains one of them, and the pool without one passive.</summary>
    internal static int[] StageOneCandidates(int union, int a, int b, int fullMask = -1)
    {
        var set = new HashSet<int> { union };
        if (a != 0) set.Add(a);
        if (b != 0) set.Add(b);
        for (var sub = union; ; sub = (sub - 1) & union)
        {
            if ((a != 0 && (sub & a) == a) || (b != 0 && (sub & b) == b)) set.Add(sub);
            if (sub == 0) break;
        }

        // "One short" sets (T100): the pool without one wanted passive that both parents hold. Such a set contains neither parent's set, so
        // the rules above never offer it; a child may carry all but that one (a later partner brings it back) and keeps the better odds of the
        // smaller set. A passive only one parent holds is not dropped: the set without it contains the other parent's set already. Only when
        // one parent holds the whole pool, the other at least two of its wanted passives, and the pool holds all of them (the case the fast stage missed): at most the number of wanted
        // passives extra candidates, and none for the cheap pools, so the stage keeps its time.
        for (var bit = 1; bit <= union && BitOperations.PopCount((uint)a) >= 2 && BitOperations.PopCount((uint)b) >= 2 && (a == union || b == union) && (fullMask < 0 || union == fullMask); bit <<= 1)
            if ((a & bit) != 0 && (b & bit) != 0 && (union & ~bit) != 0) set.Add(union & ~bit);

        return set.ToArray();
    }

    private PlanResult Run(GoalQuery q, IReadOnlyList<OwnedPal> owned, CancellationToken ct, IProgress<SolverProgress>? progress, RunOptions opt)
    {
        var watch = opt.Watch;
        var phase2 = opt.Phase2;
        if (!_data.TryNormalizePalId(q.TargetId, out var targetId)) throw new KeyNotFoundException($"Unknown Pal id '{q.TargetId}'.");
        var desired = q.DesiredPassives.Distinct(StringComparer.OrdinalIgnoreCase).Take(4).Select(id => _data.Passive(id).Id).ToList();
        var fullMask = (1 << desired.Count) - 1;
        var maxUnwanted = Math.Clamp(q.MaxUnwanted, 0, 3);
        var maxResults = Math.Max(1, q.MaxResults);
        var exclusions = PlanExclusions.Create(_data, q, targetId);
        var scoped = exclusions.Filter(OwnedPalRepository.Filter(owned, q.Scope));

        var pals = _data.Pals;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pals.Count; i++) index[pals[i].Id] = i;
        var target = index[targetId];

        int MaskOf(OwnedPal p)
        {
            var mask = 0;
            for (var i = 0; i < desired.Count; i++)
                if (p.Passives.Contains(desired[i], StringComparer.OrdinalIgnoreCase)) mask |= 1 << i;
            return mask;
        }

        string[] UnwantedOf(OwnedPal p) => p.Passives.Where(x => !desired.Contains(x, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        bool GenderFits(OwnedPal p) => q.TargetGender switch
        {
            GenderReq.Male => p.Gender is null or Gender.Male,
            GenderReq.Female => p.Gender is null or Gender.Female,
            _ => true,
        };

        // An owned target no longer ends the search (T43): it is reported as a note (how many already have the wanted passives and gender) and may
        // be a parent; plans always make a new target in at least one step.
        var ownedTargets = scoped.Where(p => string.Equals(p.SpeciesId, targetId, StringComparison.OrdinalIgnoreCase))
            .BestFirst().ToList();
        var ownedMatching = ownedTargets.Count(p => MaskOf(p) == fullMask && UnwantedOf(p).Length <= maxUnwanted && GenderFits(p));

        // ---- leaves ------------------------------------------------------------------------------------------------
        var preferred = q.PreferredUids.ToHashSet(StringComparer.Ordinal);
        var startSpecies = q.Start?.SpeciesId != null && _data.TryNormalizePalId(q.Start.SpeciesId, out var sid) ? sid : null;
        var needStart = q.Start != null;
        var needPreferred = q.PreferredMode == PreferredMode.AtLeastOne && preferred.Count > 0;

        var groups = new List<LeafGroup>();
        foreach (var g in scoped.SelectMany(p => (p.Gender is { } one ? new[] { one } : new[] { Gender.Male, Gender.Female })
                         .Select(gender => (Pal: p, Gender: gender)))
                     .GroupBy(x => (Species: x.Pal.SpeciesId.ToUpperInvariant(), x.Gender, Have: MaskOf(x.Pal),
                         Ids: string.Join("|", UnwantedOf(x.Pal).Order(StringComparer.Ordinal)),
                         Start: (q.Start?.OwnedUid != null && x.Pal.Uid == q.Start.OwnedUid) ||
                                (startSpecies != null && string.Equals(x.Pal.SpeciesId, startSpecies, StringComparison.OrdinalIgnoreCase)),
                         Preferred: preferred.Contains(x.Pal.Uid))))
        {
            var members = g.Select(x => x.Pal).BestFirst().ToList();
            groups.Add(new LeafGroup
            {
                SpeciesId = members[0].SpeciesId, Gender = g.Key.Gender, Have = g.Key.Have, UnwantedIds = UnwantedOf(members[0]),
                Start = g.Key.Start, Preferred = g.Key.Preferred, Pals = members,
            });
        }

        // Dominated owned groups are dropped (T111): of two groups of the same species, gender, start and preferred flags, the one with fewer wanted
        // passives and no fewer unwanted ones can never give a cheaper plan. One Pal may serve several steps (T106), so no spare copy is needed.
        groups = groups
            .GroupBy(g => (Species: g.SpeciesId.ToUpperInvariant(), g.Gender, g.Start, g.Preferred))
            .SelectMany(bucket =>
            {
                var list = bucket.ToList();
                return list.Where(g => !list.Any(o => !ReferenceEquals(o, g) && (o.Have & g.Have) == g.Have &&
                                                      o.UnwantedIds.All(x => g.UnwantedIds.Contains(x, StringComparer.OrdinalIgnoreCase)) &&
                                                      (o.Have != g.Have || o.UnwantedIds.Length < g.UnwantedIds.Length) &&
                                                      (o.Pals.Count >= 2 || o.Pals.Any(p => p.Gender != null) ||
                                                       g.Pals.All(gp => o.Pals.Any(op => op.Uid == gp.Uid)))));
            })
            .ToList();

        // Wild leaves and an assumed start species (genders without an owned Pal).
        var wildSources = LeafBuilder.Build(_data, [], q.AllowWild, q.ExcludeLegendaryWild, q.Start, [], exclusions);
        foreach (var leaf in wildSources)
        {
            if (leaf.UsesStart && groups.Any(g => g.Start && g.Gender == leaf.Gender &&
                                                  string.Equals(g.SpeciesId, leaf.SpeciesId, StringComparison.OrdinalIgnoreCase))) continue;
            // A wild Pal of a species with guaranteed passives carries them (T113): the wanted ones are held, the others count as unwanted.
            var guaranteed = _data.TryNormalizePalId(leaf.SpeciesId, out var gid) ? _data.Pal(gid).GuaranteedPassives : [];
            var guaranteedMask = 0;
            for (var i = 0; i < desired.Count; i++)
                if (guaranteed.Contains(desired[i], StringComparer.OrdinalIgnoreCase)) guaranteedMask |= 1 << i;
            groups.Add(new LeafGroup
            {
                SpeciesId = leaf.SpeciesId, Gender = leaf.Gender, Have = guaranteedMask, UnwantedIds = [], Start = leaf.UsesStart, Preferred = false,
                Pals = [], Wild = 1, ObtainMethod = leaf.ObtainMethod,
                GuaranteedUnwanted = guaranteed.Count(x => !desired.Contains(x, StringComparer.OrdinalIgnoreCase)),
            });
        }

        var startAssumed = groups.Any(g => g.Wild > 0 && g.Start);
        var wildLimit = q.MaxWild + (startAssumed ? 1 : 0);

        // ---- search ------------------------------------------------------------------------------------------------
        var best = new Dictionary<long, double>();
        var bound = opt.UpperBound;     // expected eggs a state may not reach (phase 2)
        var speciesSteps = new int[pals.Count];
        Array.Fill(speciesSteps, -1);
        double emin = 0;                // cheapest possible last step (all wanted passives, nothing unwanted)
        var queue = new PriorityQueue<SolverState, (double, int, int)>();
        var buckets = new Dictionary<int, ParetoSet>();
        var settledBySpecies = new[] { new List<SolverState>?[pals.Count], new List<SolverState>?[pals.Count] };
        var activeSpecies = new[] { new HashSet<int>(), new HashSet<int>() };
        // The fewest steps and wild Pals among the settled states of a species and gender (T111): a whole species group is skipped when even its
        // cheapest state cannot be paired inside the limits.
        var minStepsOf = new[] { new int[pals.Count], new int[pals.Count] };
        // States of a species and gender that hold a wanted passive or a start/preferred flag (the "plain" ones need no pair work, T115), and how many
        // buckets hold a leaf so far (the cached "saturated" answers are worked out again when it grows).
        var notPlainOf = new[] { new int[pals.Count], new int[pals.Count] };
        var leafBuckets = 0;
        var minWildOf = new[] { new int[pals.Count], new int[pals.Count] };
        var pairInfo = new PairInfo?[pals.Count, pals.Count];
        var probCache = new double[5 * 32 * 5 * 5 * 2];     // k 0..4, u 0..31, h 0..4, w 0..4, cake: NaN until worked out (T111: no hashing in the hot loops)
        Array.Fill(probCache, double.NaN);
        // One alternative per path (T107): the canonical order of the intermediate species (or leaf pair for one-step plans); the same path with other Pals or other wild partners is one plan.
        var alternatives = new Dictionary<string, (SolverState State, double RootEggs)>(StringComparer.Ordinal);
        string SubPath(SolverState s, bool isRoot)
        {
            if (s.From == null) return string.Empty;
            var left = SubPath(s.From.A, false);
            var right = SubPath(s.From.B, false);
            var children = left.Length == 0 ? right
                : right.Length == 0 ? left
                : string.CompareOrdinal(left, right) <= 0 ? left + right : right + left;
            return isRoot ? children : children + pals[s.Species].Id + ">";
        }

        string PathKey(SolverState root)
        {
            if (root.From == null) return pals[root.Species].Id;
            var path = SubPath(root, true);
            if (path.Length > 0) return path;
            var a = pals[root.From.A.Species].Id;
            var b = pals[root.From.B.Species].Id;
            return string.CompareOrdinal(a, b) <= 0 ? $"leaf:{a}+{b}" : $"leaf:{b}+{a}";
        }
        var generated = 0L;
        var expanded = 0L;
        var timedOut = false;
        var limited = false;
        // Chance of each gender of a child, by species index (a table lookup in the hot loops instead of a string lookup per pair; T111).
        var childGender = new double[pals.Count, 2];
        for (var i = 0; i < pals.Count; i++)
        {
            childGender[i, 0] = _breeding.ChildGenderProbability(pals[i].Id, Gender.Male);
            childGender[i, 1] = _breeding.ChildGenderProbability(pals[i].Id, Gender.Female);
        }
        // A derived Pal used flexibly costs 1/p instead of 1/(p * P(gender)) (T105): LowerBound lowers the bound of the state itself (T110),
        // and flexQueue tracks the minimum LowerBound of queued flex-eligible states so queue early exits stay both tight and admissible.
        static double FlexSaving(SolverState s) =>
            s.From is { Flex: null, GenderFactor: > 0 and < 1 } r ? 1.0 / (r.Probability * r.GenderFactor) - 1.0 / r.Probability : 0;

        static long KeyOfFields(int species, int gender, int have, int unwanted, bool start, bool preferredFlag, int steps, int wild) =>
            (((((((long)species * 2 + gender) * 16 + have) * 8 + unwanted) * 2 + (start ? 1 : 0)) * 2 + (preferredFlag ? 1 : 0)) * 16 + steps) * 16 + wild;

        long KeyOf(SolverState s, int group) =>
            KeyOfFields(s.Species, s.Gender, s.Have, s.Unwanted, s.Start, s.Preferred, s.Steps, s.Wild) + ((long)(group + 1) << 40);

        static double ParentsEggs(Recipe r) => r.Flex?.ParentsEggs ?? r.A.Eggs + r.B.Eggs;

        // Cheapest last step: every wanted passive in the pool, no unwanted one, the best of normal breeding and the cake.
        {
            var kDesired = desired.Count;
            var pMax = Math.Max(_odds.Probability(kDesired, 0, kDesired, maxUnwanted, 0), q.AllowSpecialCake ? _odds.Probability(kDesired, 0, kDesired, maxUnwanted, 4) : 0);
            var rootFactor = q.TargetGender == GenderReq.Any ? 1.0
                : _breeding.ChildGenderProbability(targetId, q.TargetGender == GenderReq.Male ? Gender.Male : Gender.Female);
            emin = pMax <= 0 ? double.PositiveInfinity : 1.0 / (pMax * rootFactor);
        }

        bool IsSolution(int species, int sub, bool start, bool pref) =>
            species == target && sub == fullMask && (!needStart || start) && (!needPreferred || pref);

        // Admissible lower bound of the cost of any plan that contains the state (ARCHITECTURE 7.4).
        double LowerBound(SolverState s)
        {
            if (s.Species == target)
            {
                if (s.From == null) return s.Eggs;
                var r = s.From;
                if (IsSolution(s.Species, s.Have, s.Start, s.Preferred))
                    return ParentsEggs(r) + 1.0 / (r.Probability * (q.TargetGender == GenderReq.Any ? 1.0 : r.GenderFactor));
                return s.Eggs + emin;
            }

            if (speciesSteps[s.Species] < 0) speciesSteps[s.Species] = _breeding.MinStepsToTarget(pals[s.Species].Id, targetId);
            var steps = speciesSteps[s.Species];
            return steps == int.MaxValue ? double.PositiveInfinity : s.Eggs - FlexSaving(s) + (steps - 1) + emin;
        }

        var flexQueue = new PriorityQueue<double, double>();

        bool TryPush(SolverState s, int group)
        {
            if (phase2 && LowerBound(s) >= bound) return false;
            if (s.From != null && IsSolution(s.Species, s.Have, s.Start, s.Preferred))
            {
                if (alternatives.TryGetValue(PathKey(s), out var prev) && prev.RootEggs <= s.Eggs) return false;
                queue.Enqueue(s, (s.Eggs, s.Steps, s.Wild));
                generated++;
                return true;
            }

            var key = KeyOf(s, group);
            if (best.TryGetValue(key, out var existing) && existing <= s.Eggs) return false;
            best[key] = s.Eggs;
            if (s.From is { Flex: null, GenderFactor: > 0 and < 1 } && s.Species != target)
                flexQueue.Enqueue(s.Eggs, LowerBound(s));
            queue.Enqueue(s, (s.Eggs, s.Steps, s.Wild));
            generated++;
            return true;
        }

        for (var gi = 0; gi < groups.Count; gi++)
        {
            var leaf = groups[gi];
            if (!index.TryGetValue(leaf.SpeciesId, out var species)) continue;
            if (leaf.Wild > 0 && leaf.Wild > q.MaxWild && !leaf.Start) continue;
            var state = new SolverState
            {
                Species = species, Gender = leaf.Gender == Gender.Male ? 0 : 1, Have = leaf.Have,
                Unwanted = leaf.Wild > 0 ? Math.Clamp(Math.Max(q.WildAssumedUnwanted, leaf.GuaranteedUnwanted), 0, 4) : Math.Min(leaf.UnwantedIds.Length, 7),
                KnownUnwanted = leaf.Wild > 0 ? null : leaf.UnwantedIds,
                Start = leaf.Start, Preferred = leaf.Preferred, Steps = 0, Wild = leaf.Wild, Eggs = 0, Leaf = leaf, Group = gi,
            };
            TryPush(state, gi);
        }

        double Prob(int k, int u, int h, int w, bool cake)
        {
            if (k >= 5 || u >= 32 || h >= 5 || w >= 5) return _odds.Probability(k, u, h, w, cake ? 4 : 0);
            var key = (((k * 32 + u) * 5 + h) * 5 + w) * 2 + (cake ? 1 : 0);
            var p = probCache[key];
            if (double.IsNaN(p)) probCache[key] = p = _odds.Probability(k, u, h, w, cake ? 4 : 0);
            return p;
        }

        // Outcomes of a species pair that can still lead to the target, with the steps they need afterwards.
        PairInfo InfoOf(int a, int b)
        {
            var info = pairInfo[a, b];
            if (info != null) return info;
            var list = new List<(ChildOutcome Outcome, int Child, int Remaining)>();
            foreach (var outcome in _breeding.GetChildOutcomes(pals[a].Id, pals[b].Id))
            {
                if (exclusions.ExcludesSpecies(outcome.ChildId)) continue;
                var remaining = _breeding.MinStepsToTarget(outcome.ChildId, targetId);
                if (remaining != int.MaxValue) list.Add((outcome, index[outcome.ChildId], remaining));
            }

            info = new PairInfo(list.ToArray(), list.Count == 0 ? int.MaxValue : list.Min(x => x.Remaining));
            pairInfo[a, b] = info;
            return info;
        }

        var searchTarget = Math.Max(maxResults * 2, 4);
        var kthBest = double.PositiveInfinity;
        var bestRoot = double.PositiveInfinity;
        var iterations = 0;
        var combos = 0L;

        static int[] AllSubsets(int union)
        {
            var list = new List<int>();
            for (var sub = union; ; sub = (sub - 1) & union)
            {
                list.Add(sub);
                if (sub == 0) break;
            }

            return list.ToArray();
        }

        // Stage 1: the whole pool, each parent's own set and every set that contains one of them.
        var candidateCache = new int[1 << 12][];

        int[] Candidates(int union, int a, int b)
        {
            var key = (union << 8) | (a << 4) | b;
            return candidateCache[key] ??= StageOneCandidates(union, a, b, fullMask);
        }


        // Unwanted passives in the pool of two parents (an owned leaf knows its own, so a shared one counts once; a leaf without any needs no set).
        static int UnwantedOf2(SolverState a, SolverState b)
        {
            var x = a.KnownUnwanted;
            var y = b.KnownUnwanted;
            if (x != null && y != null)
            {
                if (x.Length == 0) return y.Length;
                if (y.Length == 0) return x.Length;
                return x.Union(y, StringComparer.OrdinalIgnoreCase).Count();
            }

            return (x?.Length ?? a.Unwanted) + (y?.Length ?? b.Unwanted);
        }

        // Flexible gender (T105, ARCHITECTURE 7.4): a derived Pal F meets a leaf P that has a backup P' (same species, opposite gender, a leaf).
        // F's gender then does not matter: a F of the gender P needs pairs with P, the other gender pairs with P'. F costs 1/p instead of
        // 1/(p * P(gender)); the step's eggs are the average over F's gender of the two pools. Only when both pools carry the child's whole
        // wanted set (the common set), so the backup never makes a step worse.
        void CombineFlex(SolverState s, SolverState t, PairChunk chunk)
        {
            SolverState f, p;
            if (s.From != null && t.From == null) { f = s; p = t; }
            else if (t.From != null && s.From == null) { f = t; p = s; }
            else return;
            if (f.Species == target || f.From!.Flex != null || f.Steps == 0) return;
            if (!phase2 && (f.Have | p.Have) != fullMask) return;   // cheap exit: the fast stage only tries the whole wanted set
            var altList = settledBySpecies[1 - p.Gender][p.Species];
            if (altList == null) return;
            var fromS = ReferenceEquals(f, s);
            var mainInfo = InfoOf(s.Species, t.Species);
            PairInfo? altInfo = null;   // looked up when a backup qualifies (it is a new species pair, so it is not free)

            var r = f.From!;
            var fEggs = f.Eggs - 1.0 / (r.Probability * r.GenderFactor) + 1.0 / r.Probability;
            var fGender = f.Gender == 0 ? Gender.Male : Gender.Female;
            var wMain = _breeding.ChildGenderProbability(pals[f.Species].Id, fGender);
            var wAlt = 1.0 - wMain;
            if (wMain <= 0 || wAlt <= 0) return;

            foreach (var alt in altList)
            {
                if (alt.From != null) continue;
                // One Pal of unknown gender sits in both gender groups: it cannot be its own backup (T110).
                if (p.Leaf != null && alt.Leaf != null && p.Leaf.Pals.Count > 0 && alt.Leaf.Pals.Count > 0 &&
                    !p.Leaf.Pals.Any(x => alt.Leaf.Pals.Any(y => y.Uid != x.Uid))) continue;
                // The main partner and the backup are alternatives (only one is met), so the plan needs the larger of the two wild counts.
                var wild = f.Wild + Math.Max(p.Wild, alt.Wild);
                if (wild > wildLimit) continue;
                var steps = f.Steps + p.Steps + 1;
                var unionMain = f.Have | p.Have;
                var unionAlt = f.Have | alt.Have;
                var common = unionMain & unionAlt;
                if (common == 0 || (!phase2 && common != fullMask)) continue; // the fast stage tries the whole wanted set only
                var kMain = BitOperations.PopCount((uint)unionMain);
                var kAlt = BitOperations.PopCount((uint)unionAlt);
                var uMain = UnwantedOf2(f, p);
                var uAlt = UnwantedOf2(f, alt);
                var start = f.Start || (p.Start && alt.Start);
                var pref = f.Preferred || (p.Preferred && alt.Preferred);
                altInfo ??= fromS ? InfoOf(p.Species, f.Species) : InfoOf(f.Species, p.Species);
                if (altInfo.MinRemaining == int.MaxValue) return;

                foreach (var (outcome, child, remaining) in mainInfo.Outcomes)
                {
                    if (!Fits(outcome.AGender, s.Gender) || !Fits(outcome.BGender, t.Gender)) continue;
                    if ((long)steps + remaining > q.MaxSteps) continue;
                    var inAlt = false;
                    foreach (var (altOutcome, altChild, _) in altInfo.Outcomes)
                        if (altChild == child && Fits(altOutcome.AGender, 0) && Fits(altOutcome.BGender, 1)) { inAlt = true; break; }
                    if (!inAlt) continue;

                    for (var g = 0; g < 2; g++)
                    {
                        var pg = childGender[child, g];
                        if (pg <= 0) continue;

                        foreach (var sub in phase2 ? AllSubsets(common) : [common])
                        {
                            if (sub == 0) continue;
                            var solution = IsSolution(child, sub, start, pref);
                            if (solution && q.TargetGender != GenderReq.Any && (q.TargetGender == GenderReq.Male) != (g == 0)) continue;
                            var factor = solution && q.TargetGender == GenderReq.Any ? 1.0 : pg;
                            var h = BitOperations.PopCount((uint)sub);
                            for (var w = 0; w <= maxUnwanted; w++)
                            {
                                var pMain = Prob(kMain, uMain, h, w, false);
                                var pAlt = Prob(kAlt, uAlt, h, w, false);
                                var flexCake = false;
                                if (q.AllowSpecialCake)
                                {
                                    var cMain = Prob(kMain, uMain, h, w, true);
                                    var cAlt = Prob(kAlt, uAlt, h, w, true);
                                    if (cMain > 0 && cAlt > 0 && wMain / cMain + wAlt / cAlt < (pMain <= 0 || pAlt <= 0 ? double.PositiveInfinity : wMain / pMain + wAlt / pAlt))
                                    {
                                        pMain = cMain; pAlt = cAlt; flexCake = true;
                                    }
                                }

                                if (pMain <= 0 || pAlt <= 0) continue;
                                if (!solution &&
                                    buckets.TryGetValue(((child * 2 + g) * 2 + (start ? 1 : 0)) * 2 + (pref ? 1 : 0), out var settledBucket) &&
                                    (settledBucket.IsFull(sub, w) || settledBucket.IsDominated(sub, w, steps, wild))) continue;
                                var perEgg = wMain / pMain + wAlt / pAlt;     // expected eggs per success, before the child's gender
                                var stepEggs = perEgg / factor;
                                var parents = fEggs + p.Eggs;
                                var state = new SolverState
                                {
                                    Species = child, Gender = g, Have = sub, Unwanted = w, Start = start, Preferred = pref,
                                    Steps = steps, Wild = wild, Eggs = parents + stepEggs,
                                    From = new Recipe(s, t, child, g, sub, w, flexCake, 1.0 / perEgg, factor, new FlexInfo(alt, fromS, parents, stepEggs)),
                                };
                                chunk.States.Add(state);
                            }
                        }
                    }
                }
            }
        }

        static bool HasDistinctOwnedPair(SolverState a, SolverState b)
        {
            if (a.From != null || b.From != null || a.Leaf == null || b.Leaf == null ||
                a.Leaf.Pals.Count == 0 || b.Leaf.Pals.Count == 0) return true;
            return a.Leaf.Pals.Any(x => b.Leaf.Pals.Any(y => y.Uid != x.Uid));
        }

        // The candidate states of a set of pairs (T119). Combine and CombineFlex only READ the search state (settled buckets and states, caches that
        // fill with the same values whoever fills them, the cheapest known cost per kind) and add what they make to the chunk; the main loop pushes the
        // chunks in order, so the result is the same whatever the number of threads.
        void Combine(SolverState s, SolverState t, PairInfo info, PairChunk chunk)
        {
            if ((++chunk.Combos & 1023) == 0 && ct.IsCancellationRequested) return;   // the loop that called us throws after the join
            CombineFlex(s, t, chunk);
            if (!HasDistinctOwnedPair(s, t)) return;
            var wild = s.Wild + t.Wild;
            if (wild > wildLimit) return;
            var steps = s.Steps + t.Steps + 1;
            if ((long)steps + info.MinRemaining > q.MaxSteps) return;
            var union = s.Have | t.Have;
            var k = BitOperations.PopCount((uint)union);
            var u = UnwantedOf2(s, t);
            var start = s.Start || t.Start;
            var pref = s.Preferred || t.Preferred;

            foreach (var (outcome, child, remaining) in info.Outcomes)
            {
                if (!Fits(outcome.AGender, s.Gender) || !Fits(outcome.BGender, t.Gender)) continue;
                if ((long)steps + remaining > q.MaxSteps) continue;

                for (var g = 0; g < 2; g++)
                {
                    var pg = childGender[child, g];
                    if (pg <= 0) continue;

                    // The child's bucket does not change while the pair is combined (states settle in the main loop only).
                    buckets.TryGetValue(((child * 2 + g) * 2 + (start ? 1 : 0)) * 2 + (pref ? 1 : 0), out var settledBucket);

                    // Every subset H of the wanted passives the pool holds, every allowed number of unwanted ones.
                    // Candidate sets: everything the pool holds, or what one parent holds (the useful partial targets).
                    foreach (var sub in phase2 ? AllSubsets(union) : Candidates(union, s.Have, t.Have))
                    {
                        var solution = IsSolution(child, sub, start, pref);
                        if (solution && q.TargetGender != GenderReq.Any && (q.TargetGender == GenderReq.Male) != (g == 0)) continue;
                        var factor = solution && q.TargetGender == GenderReq.Any ? 1.0 : pg;
                        var h = BitOperations.PopCount((uint)sub);
                        // A child without wanted passives is only a partner: assume the loosest acceptance (w = maximum),
                        // which keeps the search small and can only underestimate the later odds.
                        // A "one short" set (T100, stage 1 only) is tried with no unwanted passive: it is a partial target and stays cheap.
                        var oneShort = !phase2 && sub != 0 && (sub & s.Have) != s.Have && (sub & t.Have) != t.Have;
                        // ...and only for a child of two Pals you have that is a parent of the last step (the later partner brings the missing passive): deeper cases are left to the improving stage.
                        if (oneShort && (remaining > 1 || s.Steps + t.Steps > 0)) continue;
                        for (var w = !phase2 && sub == 0 ? maxUnwanted : 0; w <= (oneShort ? 0 : maxUnwanted); w++)
                        {
                            var normal = Prob(k, u, h, w, false);
                            var cake = q.AllowSpecialCake ? Prob(k, u, h, w, true) : 0;
                            var useCake = cake > normal;
                            var p = useCake ? cake : normal;
                            if (p <= 0) continue;
                            // Already beaten by a cheaper settled state of the same kind: not worth creating (complete target solutions are kept per path).
                            if (!solution && settledBucket != null &&
                                ((!phase2 && sub == 0 && settledBucket.HasLeaf) || settledBucket.IsFull(sub, w) || settledBucket.IsDominated(sub, w, steps, wild))) continue;
                            var eggs = s.Eggs + t.Eggs + 1.0 / (p * factor);
                            // Cheaper or equal states of the same kind already exist: TryPush would drop this one (the cheapest known cost only goes down).
                            if (!solution &&
                                best.TryGetValue(KeyOfFields(child, g, sub, w, start, pref, steps, wild), out var known) && known <= eggs) continue;
                            var state = new SolverState
                            {
                                Species = child, Gender = g, Have = sub, Unwanted = w, Start = start, Preferred = pref,
                                Steps = steps, Wild = wild, Eggs = eggs,
                                From = new Recipe(s, t, child, g, sub, w, useCake, p, factor),
                            };
                            if (phase2 && LowerBound(state) >= bound) continue;   // TryPush would drop it
                            chunk.States.Add(state);
                        }

                    }
                }
            }
        }

        // T115, species pair level: in the fast stage a pair of plain states (no wanted passive, no flag) can only make the child states with no wanted
        // passive, and those are dropped when their bucket holds a leaf already. When that is so for every child of the species pair, the pair is skipped
        // without working out a single chance. The same test Combine makes per pair, asked once per species pair; leaves only get more, so a yes stays.
        bool Saturated(PairInfo info)
        {
            if (info.Saturated) return true;
            if (info.CheckedAt == leafBuckets) return false;
            var all = true;
            foreach (var (_, child, _) in info.Outcomes)
            {
                for (var g = 0; g < 2 && all; g++)
                {
                    if (childGender[child, g] <= 0) continue;
                    if (!buckets.TryGetValue(((child * 2 + g) * 2) * 2, out var bucket) || !bucket.HasLeaf) all = false;
                }

                if (!all) break;
            }

            if (all) info.Saturated = true; else info.CheckedAt = leafBuckets;
            return all;
        }

        // The pairs of one expansion, species by species (in the order of the active species), in chunks that are made in parallel when there is
        // enough work and merged in order (T119). A species' pair info, its limits and its settled states are all looked at inside the chunk.
        PairChunk[] CombineAll(SolverState current)
        {
            var otherGender = 1 - current.Gender;
            var currentPlain = !phase2 && fullMask != 0 && current.Have == 0 && !current.Start && !current.Preferred;
            var species = activeSpecies[otherGender].ToArray();
            long total = 0;
            foreach (var sp in species) total += settledBySpecies[otherGender][sp]!.Count;
            var workers = opt.Threads <= 1 ? 1 : (int)Math.Min(opt.Threads, total / MinPairsPerWorker);
            var parts = workers <= 1 ? 1 : (int)Math.Min(species.Length, workers * 3L);
            var chunks = new PairChunk[parts];
            for (var i = 0; i < parts; i++) chunks[i] = new PairChunk();

            void RunPart(int part)
            {
                var chunk = chunks[part];
                var from = (int)((long)species.Length * part / parts);
                var to = (int)((long)species.Length * (part + 1) / parts);
                for (var i = from; i < to; i++)
                {
                    var otherSpecies = species[i];
                    var info = current.Gender == 0 ? InfoOf(current.Species, otherSpecies) : InfoOf(otherSpecies, current.Species);
                    if (info.MinRemaining == int.MaxValue) continue;
                    if ((long)current.Steps + minStepsOf[otherGender][otherSpecies] + 1 + info.MinRemaining > q.MaxSteps) continue;
                    if (current.Wild + minWildOf[otherGender][otherSpecies] > wildLimit) continue;
                    // Plain with plain (T115): nothing a pair of them makes would be kept when the species pair is saturated.
                    var skipPlain = currentPlain && Saturated(info);
                    if (skipPlain && notPlainOf[otherGender][otherSpecies] == 0) continue;
                    foreach (var other in settledBySpecies[otherGender][otherSpecies]!)
                    {
                        if (skipPlain && other.Have == 0 && !other.Start && !other.Preferred) continue;
                        if (current.Gender == 0) Combine(current, other, info, chunk); else Combine(other, current, info, chunk);
                        if (ct.IsCancellationRequested) return;
                    }
                }
            }

            if (parts == 1) RunPart(0);
            else Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = workers }, RunPart);
            return chunks;
        }

        while (queue.TryDequeue(out var current, out _))
        {
            if ((++iterations & 127) == 0)
            {
                ct.ThrowIfCancellationRequested();
                if (opt.Limit is { } limit && watch.Elapsed > limit) { timedOut = true; break; }
                if ((iterations & 2047) == 0)
                    progress?.Report(new SolverProgress("search", Math.Min(0.99, expanded / 200000.0), expanded,
                        double.IsPositiveInfinity(kthBest) ? null : kthBest));
            }

            var isSolution = current.From != null && IsSolution(current.Species, current.Have, current.Start, current.Preferred);
            if (!isSolution && best.TryGetValue(KeyOf(current, current.Group), out var bestEggs) && current.Eggs > bestEggs + 1e-12) continue;
            while (flexQueue.TryPeek(out var topEggs, out _) && topEggs < current.Eggs) flexQueue.Dequeue();
            var minPossible = Math.Min(Math.Min(current.Eggs, LowerBound(current)),
                flexQueue.TryPeek(out _, out var minFlexLb) ? minFlexLb : double.PositiveInfinity);
            if (phase2 && minPossible >= bound) break;
            if (minPossible > kthBest) break; // nothing cheaper can still come
            // Branch and bound: alternatives far costlier than the best plan are of no use.
            if (!double.IsPositiveInfinity(bestRoot) && minPossible > bestRoot * AlternativeCostFactor + AlternativeCostMargin) break;
            if (phase2 && LowerBound(current) >= bound) continue;

            if (isSolution)
            {
                var r = current.From!;
                var rootEggs = ParentsEggs(r) + 1.0 / (r.Probability * (q.TargetGender == GenderReq.Any ? 1.0 : r.GenderFactor));
                var pair = PathKey(current);
                if (!alternatives.TryGetValue(pair, out var existing) || rootEggs < existing.RootEggs)
                    alternatives[pair] = (current, rootEggs);
                var improved = rootEggs < bestRoot - 1e-9;
                bestRoot = Math.Min(bestRoot, rootEggs);
                if (improved && phase2 && opt.OnImprove != null)
                {
                    var snapshot = alternatives.Values.OrderBy(a => a.RootEggs).Take(maxResults).Select(a => BuildPlan(a.State)).OfType<BreedingPlan>().ToList();
                    if (snapshot.Count > 0) opt.OnImprove(snapshot);
                }
                // Keep searching for a few more alternatives than asked: some may fail the leaf check when the plan is built.
                if (alternatives.Count >= searchTarget)
                    kthBest = alternatives.Values.Select(a => a.RootEggs).Order().ElementAt(searchTarget - 1);
                bound = Math.Min(bound, Math.Min(kthBest, bestRoot * AlternativeCostFactor + AlternativeCostMargin));
                continue;
            }

            if (!buckets.TryGetValue(current.BucketKey, out var bucket)) buckets[current.BucketKey] = bucket = new ParetoSet(phase2);
            if (bucket.IsDominated(current)) continue;
            // An owned Pal of the target species is a parent, never a solution: it must not dominate the target made by breeding (T110).
            if (!(current.From == null && current.Species == target))
            {
                if (current.From == null && !bucket.HasLeaf) leafBuckets++;
                bucket.Add(current);
            }
            expanded++;

            if (activeSpecies[current.Gender].Add(current.Species))
            {
                settledBySpecies[current.Gender][current.Species] = [];
                minStepsOf[current.Gender][current.Species] = int.MaxValue;
                minWildOf[current.Gender][current.Species] = int.MaxValue;
            }

            settledBySpecies[current.Gender][current.Species]!.Add(current);
            if (current.Have != 0 || current.Start || current.Preferred) notPlainOf[current.Gender][current.Species]++;
            minStepsOf[current.Gender][current.Species] = Math.Min(minStepsOf[current.Gender][current.Species], current.Steps);
            minWildOf[current.Gender][current.Species] = Math.Min(minWildOf[current.Gender][current.Species], current.Wild);
            if (limited) continue;
            foreach (var chunk in CombineAll(current))
            {
                combos += chunk.Combos;
                foreach (var state in chunk.States)
                {
                    if (generated < StateLimit) TryPush(state, -1);
                    else limited = true;
                }
            }

            ct.ThrowIfCancellationRequested();
        }

        if (opt.Record) LastStats = new SolverStats(generated, expanded, combos, watch.ElapsedMilliseconds);
        progress?.Report(new SolverProgress("assemble", 1, expanded, double.IsPositiveInfinity(kthBest) ? null : kthBest));

        // ---- plans ---------------------------------------------------------------------------------------------------
        var plans = new List<BreedingPlan>();
        foreach (var alt in alternatives.Values.OrderBy(a => a.RootEggs).ThenBy(a => a.State.Steps))
        {
            if (plans.Count >= maxResults) break;
            var plan = BuildPlan(alt.State);
            if (plan != null) plans.Add(plan);
        }

        var diagnostics = new List<string>();
        if (timedOut) diagnostics.Add(PlanNotes.TimedOut);
        if (limited) diagnostics.Add(PlanNotes.StateLimit);
        if (plans.Count == 0)
        {
            diagnostics.Add(PlanNotes.NoPlan);
            if (!q.AllowWild) diagnostics.Add(PlanNotes.WildOff);
            diagnostics.AddRange(PlanDiagnostics.Explain(_data, scoped, targetId, desired, q.AllowWild, q.ExcludeLegendaryWild, exclusions));
            // Out of time before any plan: the search did not finish, so nothing can be said about the wild limit, the start Pal or the steps (T106).
            if (timedOut) diagnostics.Add(PlanNotes.TimedOutNoPlan);
            else
            {
                var minSteps = groups.Select(g => _breeding.MinStepsToTarget(g.SpeciesId, targetId)).DefaultIfEmpty(int.MaxValue).Min();
                if (minSteps != int.MaxValue && minSteps > q.MaxSteps) diagnostics.Add(PlanNotes.StepLimit);
                else if (q.AllowWild && q.MaxWild < 4) diagnostics.Add(PlanNotes.WildLimit);
                if (needStart) diagnostics.Add(PlanNotes.StartUnused);
                if (needPreferred) diagnostics.Add(PlanNotes.PreferredUnused);
            }
        }

        var ordered = plans.OrderBy(p => p.ExpectedEggsTotal).ThenBy(p => p.Steps).ToList();
        return new PlanResult(ordered, [], diagnostics.Distinct().ToList(), timedOut, ownedTargets, ownedMatching);

        // -----------------------------------------------------------------------------------------------------------------
        static bool Fits(GenderReq required, int gender) =>
            required == GenderReq.Any || (required == GenderReq.Male ? gender == 0 : gender == 1);

        BreedingPlan? BuildPlan(SolverState root)
        {
            var notes = new List<string>();
            var node = Expand(root, notes, isRoot: true);
            if (node == null) return null;
            if (!PlanTreeValidator.IsValid(node)) return null;
            var leaves = PlanTreeValidator.Leaves(node).ToList();
            var eggs = TotalEggs(node);
            var preferredUsed = leaves.OfType<OwnedLeaf>().Select(l => l.Pal.Uid).Where(preferred.Contains).Distinct().ToList();
            return new BreedingPlan(node, PlanTreeValidator.CountSteps(node), eggs, PlanTreeValidator.CountWild(node), root.Start,
                preferredUsed, notes.Distinct().ToList());
        }

        static (OwnedPal A, OwnedPal B)? PickDistinctPair(IReadOnlyList<OwnedPal> aPals, IReadOnlyList<OwnedPal> bPals)
        {
            if (aPals.Count == 0 || bPals.Count == 0) return null;
            var firstA = aPals[0];
            foreach (var b in bPals)
                if (b.Uid != firstA.Uid) return (firstA, b);
            var firstB = bPals[0];
            foreach (var a in aPals)
                if (a.Uid != firstB.Uid) return (a, firstB);
            return null;
        }

        static bool IsOwnedLeaf(SolverState s) => s.From == null && s.Leaf != null && s.Leaf.Pals.Count > 0;

        PlanNode ExpandLeaf(LeafGroup leaf, List<string> notes, OwnedPal? chosen = null)
        {
            var gender = leaf.Gender == Gender.Male ? GenderReq.Male : GenderReq.Female;
            if (leaf.Pals.Count == 0)
            {
                var species = _data.Pal(leaf.SpeciesId);
                if (species.BreedingFlags.SameSpeciesOnly) notes.Add($"{PlanNotes.SameSpeciesWild}|{species.Id}");
                if (leaf.Start) notes.Add($"{PlanNotes.StartAssumed}|{species.Id}");
                return new WildLeaf(leaf.SpeciesId, gender, Math.Max(0, Math.Max(q.WildAssumedUnwanted, leaf.GuaranteedUnwanted)), leaf.ObtainMethod ?? "Wild");
            }

            // Breeding does not consume a parent and the steps run in order: the same Pal may serve several steps (T106).
            return new OwnedLeaf(chosen ?? leaf.Pals[0]);
        }

        PlanNode? Expand(SolverState s, List<string> notes, bool isRoot, bool flexible = false, OwnedPal? forcedOwned = null)
        {
            if (s.From == null) return ExpandLeaf(s.Leaf!, notes, forcedOwned);

            var r = s.From;
            OwnedPal? forcedA = null;
            OwnedPal? forcedB = null;
            OwnedPal? forcedAlt = null;

            if (IsOwnedLeaf(r.A) && IsOwnedLeaf(r.B))
            {
                if (PickDistinctPair(r.A.Leaf!.Pals, r.B.Leaf!.Pals) is not { } pair) return null;
                forcedA = pair.A;
                forcedB = pair.B;
            }

            var a = Expand(r.A, notes, false, r.Flex is { FlexIsA: true }, forcedA);
            if (a == null) return null;
            var b = Expand(r.B, notes, false, r.Flex is { FlexIsA: false }, forcedB);
            if (b == null) return null;

            PlanNode? altPartner = null;
            if (r.Flex != null)
            {
                var mainLeafState = r.Flex.FlexIsA ? r.B : r.A;
                if (IsOwnedLeaf(mainLeafState) && IsOwnedLeaf(r.Flex.Alt))
                {
                    var mainPal = r.Flex.FlexIsA ? (b as OwnedLeaf)?.Pal : (a as OwnedLeaf)?.Pal;
                    if (mainPal != null)
                    {
                        var distinctAlt = r.Flex.Alt.Leaf!.Pals.FirstOrDefault(p => p.Uid != mainPal.Uid);
                        if (distinctAlt != null)
                        {
                            forcedAlt = distinctAlt;
                        }
                        else if (PickDistinctPair(mainLeafState.Leaf!.Pals, r.Flex.Alt.Leaf.Pals) is { } altPair)
                        {
                            if (r.Flex.FlexIsA) b = ExpandLeaf(mainLeafState.Leaf, notes, altPair.A);
                            else a = ExpandLeaf(mainLeafState.Leaf, notes, altPair.A);
                            forcedAlt = altPair.B;
                        }
                        else return null;
                    }
                }

                altPartner = Expand(r.Flex.Alt, notes, false, false, forcedAlt);
                if (altPartner == null) return null;
                var flexible2 = r.Flex.FlexIsA ? a : b;
                notes.Add($"{PlanNotes.FlexGender}|{flexible2.SpeciesId}|{altPartner.SpeciesId}|{(altPartner.Gender == GenderReq.Male ? "Male" : "Female")}");
            }

            var factor = flexible || (isRoot && q.TargetGender == GenderReq.Any) ? 1.0 : r.GenderFactor;
            var success = r.Probability * factor;
            var passives = Enumerable.Range(0, desired.Count).Where(i => (r.Have & (1 << i)) != 0).Select(i => desired[i]).ToList();
            var nodeGender = isRoot ? q.TargetGender : flexible ? GenderReq.Any : (r.ChildGender == 0 ? GenderReq.Male : GenderReq.Female);
            return new BreedStep(a, b, pals[r.ChildSpecies].Id, nodeGender, passives, r.Unwanted, r.Cake ? SpecialCakeId : null, success, 1.0 / success, altPartner);
        }

        static double TotalEggs(PlanNode n) => n is BreedStep s ? s.ExpectedEggs + TotalEggs(s.ParentA) + TotalEggs(s.ParentB) : 0;
    }
}
