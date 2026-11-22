using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.Core.Planning;

/// <summary>
/// Species-level path finder (ARCHITECTURE 7.3): Knuth's generalized Dijkstra over
/// (species, gender, uses start parent, uses preferred Pal) states with the cost (steps, wild, eggs).
/// </summary>
public sealed class SpeciesPathFinder : ISpeciesPathFinder
{
    private readonly IGameData _data;
    private readonly IBreedingCalculator _breeding;

    public SpeciesPathFinder(IGameData data, IBreedingCalculator breeding)
    {
        _data = data;
        _breeding = breeding;
    }

    public Task<PlanResult> FindAsync(
        PathQuery query, IReadOnlyList<OwnedPal> owned, CancellationToken ct, IProgress<SolverProgress>? progress = null) =>
        Task.Run(() => Find(query, owned, ct, progress), ct);

    private readonly record struct Cost(int Steps, int Wild, double Eggs) : IComparable<Cost>
    {
        public static readonly Cost Infinite = new(int.MaxValue, int.MaxValue, double.MaxValue);

        public int CompareTo(Cost other)
        {
            var c = Steps.CompareTo(other.Steps);
            if (c != 0) return c;
            c = Wild.CompareTo(other.Wild);
            return c != 0 ? c : Eggs.CompareTo(other.Eggs);
        }

        public static bool operator <(Cost a, Cost b) => a.CompareTo(b) < 0;
        public static bool operator >(Cost a, Cost b) => a.CompareTo(b) > 0;
    }

    // A way to reach a state: two parent states and the child gender.
    private sealed record Recipe(int A, int B, int ChildSpecies, int ChildGender, Cost Cost);

    /// <summary>The intermediate species of a plan in order (the target is not part of it), or the parent pair for a one-step plan: the plan's path.</summary>
    internal static string PathKeyOf(PlanNode root)
    {
        static string SubPath(PlanNode n, bool isRoot)
        {
            if (n is not BreedStep s) return string.Empty;
            var left = SubPath(s.ParentA, false);
            var right = SubPath(s.ParentB, false);
            var children = left.Length == 0 ? right
                : right.Length == 0 ? left
                : string.CompareOrdinal(left, right) <= 0 ? left + right : right + left;
            return isRoot ? children : children + s.ChildSpeciesId + ">";
        }

        if (root is not BreedStep rootStep) return root.SpeciesId;
        var path = SubPath(rootStep, true);
        if (path.Length > 0) return path;
        var a = rootStep.ParentA.SpeciesId;
        var b = rootStep.ParentB.SpeciesId;
        return string.CompareOrdinal(a, b) <= 0 ? $"leaf:{a}+{b}" : $"leaf:{b}+{a}";
    }

    private IReadOnlyList<ExclusionBlocker> BlockersOf(PathQuery q, IReadOnlyList<OwnedPal> owned, CancellationToken ct)
    {
        var all = PlanExclusions.ListOf(q.ExcludedUids, q.ExcludedSpecies, q.ExcludedGroups);
        var blockers = new List<ExclusionBlocker>();
        foreach (var exclusion in all.Skip(Math.Max(0, all.Count - 4)))
        {
            ct.ThrowIfCancellationRequested();
            var without = q with
            {
                ExcludedUids = q.ExcludedUids?.Where(u => !(exclusion.Kind == PlanExclusion.UidKind && u == exclusion.Id)).ToList(),
                ExcludedSpecies = q.ExcludedSpecies?.Where(s => !(exclusion.Kind == PlanExclusion.SpeciesKind && s == exclusion.Id)).ToList(),
                ExcludedGroups = q.ExcludedGroups?.Where(g => !(exclusion.Kind == PlanExclusion.GroupKind && g == exclusion.Id)).ToList(),
                MaxResults = 1, PreferredUids = [],
            };
            var result = FindCore(without, owned, ct, null, withBlockers: false);
            if (result.Plans.Count > 0) blockers.Add(new ExclusionBlocker(exclusion, result.Plans[0].ExpectedEggsTotal, result.Plans[0].Steps));
        }

        return blockers;
    }

    public PlanResult Find(PathQuery q, IReadOnlyList<OwnedPal> owned, CancellationToken ct, IProgress<SolverProgress>? progress = null) =>
        FindCore(q, owned, ct, progress, withBlockers: true);

    // withBlockers: false for the runs the finder starts itself (a preferred Pal on its own, an exclusion lifted): no blockers inside blockers.
    private PlanResult FindCore(PathQuery q, IReadOnlyList<OwnedPal> owned, CancellationToken ct, IProgress<SolverProgress>? progress, bool withBlockers)
    {
        if (!_data.TryNormalizePalId(q.TargetId, out var targetId))
            throw new KeyNotFoundException($"Unknown Pal id '{q.TargetId}'.");
        var pals = _data.Pals;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pals.Count; i++) index[pals[i].Id] = i;
        var target = index[targetId];
        var maxResults = Math.Max(1, q.MaxResults);

        var exclusions = PlanExclusions.Create(_data, q, targetId);
        var scoped = exclusions.Filter(OwnedPalRepository.Filter(owned, q.Scope));

        // An owned target does not end the search (T43): it is reported as a note and may be a parent (male + female target -> one step); plans
        // always make a NEW target in at least one step.
        var ownedTargets = scoped.Where(p => string.Equals(p.SpeciesId, targetId, StringComparison.OrdinalIgnoreCase))
            .BestFirst().ToList();
        var wantedGender = q.TargetGender;

        var preferredSet = q.PreferredUids.ToHashSet(StringComparer.Ordinal);
        var leaves = LeafBuilder.Build(_data, scoped, q.AllowWild, q.ExcludeLegendaryWild, q.Start, q.PreferredUids, exclusions);
        var needStart = q.Start != null;
        // With "use preferred Pals" off the preferred Pals are not required (T45); each one is then checked on its own below.
        var needPreferred = preferredSet.Count > 0 && q.PreferredMode == PreferredMode.AtLeastOne;
        var startAssumed = leaves.Any(l => l.UsesStart && l.Wild > 0);
        var wildLimit = q.MaxWild + (startAssumed ? 1 : 0);

        int Key(int species, int gender, bool start, bool pref) => ((species * 2 + gender) * 2 + (start ? 1 : 0)) * 2 + (pref ? 1 : 0);
        var stateCount = pals.Count * 8;
        var dist = new Cost[stateCount];
        Array.Fill(dist, Cost.Infinite);
        var settled = new bool[stateCount];
        var best = new Recipe?[stateCount];
        var leafOf = new LeafSource?[stateCount];
        var queue = new PriorityQueue<int, Cost>();

        foreach (var leaf in leaves)
        {
            if (!index.TryGetValue(leaf.SpeciesId, out var species)) continue;
            if (leaf.Wild > q.MaxWild && !(leaf.UsesStart && leaf.Wild > 0)) continue;
            var key = Key(species, leaf.Gender == Gender.Male ? 0 : 1, leaf.UsesStart, leaf.UsesPreferred);
            var cost = new Cost(0, leaf.Wild, 0);
            if (cost < dist[key])
            {
                dist[key] = cost;
                leafOf[key] = leaf;
                queue.Enqueue(key, cost);
            }
        }

        // Alternatives are only kept for the target: best recipe per path (canonical intermediate chain, or leaf pair for one-step plans).
        var alternatives = new Dictionary<string, Recipe>(StringComparer.Ordinal);
        var settledKeys = new List<int>();
        var iterations = 0;
        var reportEvery = Math.Max(1, stateCount / 50);

        string SubPathOfKey(int key)
        {
            var r = best[key];
            if (r == null || (leafOf[key] != null && dist[key].Steps == 0)) return string.Empty;
            var left = SubPathOfKey(r.A);
            var right = SubPathOfKey(r.B);
            var children = left.Length == 0 ? right
                : right.Length == 0 ? left
                : string.CompareOrdinal(left, right) <= 0 ? left + right : right + left;
            return children + pals[r.ChildSpecies].Id + ">";
        }

        string RecipePathKey(Recipe root)
        {
            var left = SubPathOfKey(root.A);
            var right = SubPathOfKey(root.B);
            var children = left.Length == 0 ? right
                : right.Length == 0 ? left
                : string.CompareOrdinal(left, right) <= 0 ? left + right : right + left;
            if (children.Length > 0) return children;
            var a = pals[root.A >> 3].Id;
            var b = pals[root.B >> 3].Id;
            return string.CompareOrdinal(a, b) <= 0 ? $"leaf:{a}+{b}" : $"leaf:{b}+{a}";
        }

        while (queue.TryDequeue(out var current, out var currentCost))
        {
            if (settled[current] || currentCost.CompareTo(dist[current]) != 0) continue;
            if ((++iterations & 63) == 0) ct.ThrowIfCancellationRequested();
            if (iterations % reportEvery == 0) progress?.Report(new SolverProgress("search", Math.Min(0.99, (double)iterations / stateCount)));
            settled[current] = true;

            var sSpecies = current >> 3;
            var sGender = (current >> 2) & 1;
            var sStart = ((current >> 1) & 1) == 1;
            var sPref = (current & 1) == 1;

            // A target is only used as a parent when it is a Pal the user owns (a leaf), never one made by breeding.
            var usableParent = sSpecies != target || (leafOf[current] is { Pals.Count: > 0 } && dist[current].Steps == 0);
            if (usableParent)
            {
                foreach (var other in settledKeys)
                {
                    var tGender = (other >> 2) & 1;
                    if (tGender == sGender) continue; // one male and one female
                    Combine(current, sSpecies, sGender, sStart, sPref, other);
                }
            }

            if (usableParent) settledKeys.Add(current);
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

        bool IsOwnedLeafKey(int key) =>
            (best[key] == null || (leafOf[key] != null && dist[key].Steps == 0)) && leafOf[key] is { Pals.Count: > 0 };

        void Combine(int sKey, int sSpecies, int sGender, bool sStart, bool sPref, int tKey)
        {
            if (IsOwnedLeafKey(sKey) && IsOwnedLeafKey(tKey) && PickDistinctPair(leafOf[sKey]!.Pals, leafOf[tKey]!.Pals) == null) return;
            var tSpecies = tKey >> 3;
            var tGender = (tKey >> 2) & 1;
            var tStart = ((tKey >> 1) & 1) == 1;
            var tPref = (tKey & 1) == 1;
            var cs = dist[sKey];
            var ct2 = dist[tKey];
            var wild = cs.Wild + ct2.Wild;
            if (wild > wildLimit) return;
            var steps = cs.Steps + ct2.Steps + 1;
            if (steps > q.MaxSteps) return;
            var start = sStart || tStart;
            var pref = sPref || tPref;

            foreach (var outcome in _breeding.GetChildOutcomes(pals[sSpecies].Id, pals[tSpecies].Id))
            {
                if (!Fits(outcome.AGender, sGender) || !Fits(outcome.BGender, tGender)) continue;
                if (exclusions.ExcludesSpecies(outcome.ChildId)) continue;
                var child = index[outcome.ChildId];
                var remaining = _breeding.MinStepsToTarget(outcome.ChildId, targetId);
                if (remaining == int.MaxValue || (long)steps + remaining > q.MaxSteps) continue;

                for (var g = 0; g < 2; g++)
                {
                    // The wanted gender of the target is only asked of the last step.
                    if (child == target && wantedGender != GenderReq.Any && (wantedGender == GenderReq.Male) != (g == 0)) continue;
                    var p = _breeding.ChildGenderProbability(outcome.ChildId, g == 0 ? Gender.Male : Gender.Female);
                    if (p <= 0) continue;
                    var eggs = cs.Eggs + ct2.Eggs + (child == target ? (wantedGender == GenderReq.Any ? 1.0 : 1.0 / p) : 1.0 / p);
                    var cost = new Cost(steps, wild, eggs);
                    var key = Key(child, g, start, pref);
                    var recipe = new Recipe(sKey, tKey, child, g, cost);
                    if (cost < dist[key])
                    {
                        dist[key] = cost;
                        best[key] = recipe;
                        queue.Enqueue(key, cost);
                    }

                    if (child == target)
                    {
                        // Alternatives must satisfy the start / preferred requirements.
                        if ((!needStart || start) && (!needPreferred || pref))
                        {
                            var pair = RecipePathKey(recipe);
                            if (!alternatives.TryGetValue(pair, out var existing) || cost < existing.Cost)
                                alternatives[pair] = recipe;
                        }
                    }
                }
            }
        }

        progress?.Report(new SolverProgress("assemble", 1));

        // ---- plans from the best recipes at the target, expanded into trees ----------------------------------------
        var plans = new List<BreedingPlan>();
        var all = new List<BreedingPlan>();
        // One plan per path (T107): the order of the intermediate species; the same path with other partners is the cheapest one only.
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in alternatives.Values.OrderBy(r => r.Cost))
        {
            if (plans.Count >= maxResults) break;
            var plan = BuildPlan(recipe);
            if (plan == null) continue;
            all.Add(plan);
            if (!seenPaths.Add(PathKeyOf(plan.Root))) continue;
            plans.Add(plan);
        }

        // Which preferred Pal can be used? A recipe is kept once per root species pair, so a second preferred Pal that needs the same
        // pair would look unusable; every preferred Pal therefore gets a query of its own (a single required one answers directly).
        var feasibility = new List<PreferredFeasibility>();
        foreach (var uid in q.PreferredUids.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var result = needPreferred && preferredSet.Count == 1
                ? (IReadOnlyList<BreedingPlan>)all
                : FindCore(q with { PreferredUids = [uid], PreferredMode = PreferredMode.AtLeastOne, MaxResults = 1 }, owned, ct, null, withBlockers: false).Plans;
            var usable = result.FirstOrDefault(p => p.PreferredUidsUsed.Contains(uid, StringComparer.Ordinal));
            feasibility.Add(new PreferredFeasibility(uid, usable, usable == null ? PlanNotes.NotUsable : null));
        }

        var diagnostics = new List<string>();
        if (plans.Count == 0)
        {
            diagnostics.Add(PlanNotes.NoPlan);
            if (!q.AllowWild) diagnostics.Add(PlanNotes.WildOff);
            var minSteps = leaves.Select(l => _breeding.MinStepsToTarget(l.SpeciesId, targetId)).DefaultIfEmpty(int.MaxValue).Min();
            if (minSteps != int.MaxValue && minSteps > q.MaxSteps) diagnostics.Add(PlanNotes.StepLimit);
            else if (q.AllowWild && q.MaxWild < 4) diagnostics.Add(PlanNotes.WildLimit);
            diagnostics.AddRange(PlanDiagnostics.Explain(_data, scoped, targetId, [], q.AllowWild, q.ExcludeLegendaryWild, exclusions));
            if (needStart) diagnostics.Add(PlanNotes.StartUnused);
            if (needPreferred) diagnostics.Add(PlanNotes.PreferredUnused);
        }

        var blockers = withBlockers && plans.Count == 0 && !exclusions.IsEmpty ? BlockersOf(q, owned, ct) : null;
        return new PlanResult(plans.OrderBy(p => p.Steps).ThenBy(p => p.WildCount).ThenBy(p => p.ExpectedEggsTotal).ToList(),
            feasibility, diagnostics, false, ownedTargets, 0, blockers);

        // ----------------------------------------------------------------------------------------------------------
        static bool Fits(GenderReq required, int gender) =>
            required == GenderReq.Any || (required == GenderReq.Male ? gender == 0 : gender == 1);

        BreedingPlan? BuildPlan(Recipe root)
        {
            var notes = new List<string>();
            OwnedPal? forcedA = null;
            OwnedPal? forcedB = null;
            if (IsOwnedLeafKey(root.A) && IsOwnedLeafKey(root.B))
            {
                if (PickDistinctPair(leafOf[root.A]!.Pals, leafOf[root.B]!.Pals) is not { } pair) return null;
                forcedA = pair.A;
                forcedB = pair.B;
            }
            var a = Expand(root.A, notes, forcedA);
            if (a == null) return null;
            var b = Expand(root.B, notes, forcedB);
            if (b == null) return null;

            var rootProbability = wantedGender == GenderReq.Any ? 1.0
                : _breeding.ChildGenderProbability(pals[root.ChildSpecies].Id, wantedGender == GenderReq.Male ? Gender.Male : Gender.Female);
            var step = new BreedStep(a, b, pals[root.ChildSpecies].Id, wantedGender, [], 0, null, rootProbability, 1.0 / rootProbability);
            if (!PlanTreeValidator.IsValid(step)) return null;

            var eggs = TotalEggs(step);
            var leavesOfPlan = PlanTreeValidator.Leaves(step).ToList();
            var wild = PlanTreeValidator.CountWild(step);
            var preferredUsed = leavesOfPlan.OfType<OwnedLeaf>().Select(l => l.Pal.Uid).Where(preferredSet.Contains).Distinct().ToList();
            var startKey = ((root.A >> 1) & 1) == 1 || ((root.B >> 1) & 1) == 1;
            return new BreedingPlan(step, PlanTreeValidator.CountSteps(step), eggs, wild, startKey, preferredUsed, notes.Distinct().ToList());
        }

        // Turns a state into a tree node using its best recipe, handing out distinct owned Pals within each step.
        PlanNode? Expand(int key, List<string> notes, OwnedPal? forcedOwned = null)
        {
            var recipe = best[key];
            if (recipe == null || (leafOf[key] != null && dist[key].Steps == 0))
            {
                var leaf = leafOf[key];
                if (leaf == null) return null;
                var gender = leaf.Gender == Gender.Male ? GenderReq.Male : GenderReq.Female;
                if (leaf.Pals.Count == 0)
                {
                    var species = _data.Pal(leaf.SpeciesId);
                    if (species.BreedingFlags.SameSpeciesOnly) notes.Add($"{PlanNotes.SameSpeciesWild}|{species.Id}");
                    if (leaf.UsesStart) notes.Add($"{PlanNotes.StartAssumed}|{species.Id}");
                    return new WildLeaf(leaf.SpeciesId, gender, 0, leaf.ObtainMethod ?? "Wild");
                }

                // Breeding does not use a parent up and the steps run in order: the same Pal may serve several steps (T106, T110).
                return new OwnedLeaf(forcedOwned ?? leaf.Pals[0]);
            }

            OwnedPal? forcedA = null;
            OwnedPal? forcedB = null;
            if (IsOwnedLeafKey(recipe.A) && IsOwnedLeafKey(recipe.B))
            {
                if (PickDistinctPair(leafOf[recipe.A]!.Pals, leafOf[recipe.B]!.Pals) is not { } pair) return null;
                forcedA = pair.A;
                forcedB = pair.B;
            }
            var left = Expand(recipe.A, notes, forcedA);
            if (left == null) return null;
            var right = Expand(recipe.B, notes, forcedB);
            if (right == null) return null;
            var childGender = recipe.ChildGender == 0 ? Gender.Male : Gender.Female;
            var p = _breeding.ChildGenderProbability(pals[recipe.ChildSpecies].Id, childGender);
            return new BreedStep(left, right, pals[recipe.ChildSpecies].Id,
                childGender == Gender.Male ? GenderReq.Male : GenderReq.Female, [], 0, null, p, 1.0 / p);
        }

        static double TotalEggs(PlanNode node) =>
            node is BreedStep s ? s.ExpectedEggs + TotalEggs(s.ParentA) + TotalEggs(s.ParentB) : 0;
    }
}
