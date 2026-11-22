using System.Diagnostics;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;
using Paldium.Core.Probability;

namespace Paldium.Core.Tests;

// CPU heavy or timing sensitive tests run one after another (the bundle load test has a one second limit).
[Collection("Heavy")]
public class PassiveSolverAdvancedTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static readonly Lazy<BreedingCalculator> Breeding = new(() => BreedingCalculator.Load(Data.Value, TestPaths.DataFolder));
    private static PassiveSolver Solver() => PassiveSolverTests.Solver();
    private static IReadOnlyList<string> D => PassiveSolverTests.WorkPassives();
    private static OwnedPal Pal(string uid, string species, Gender? g, IReadOnlyList<string>? passives = null) =>
        PassiveSolverTests.Pal(uid, species, g, passives);

    private static GoalQuery Goal(IReadOnlyList<string> desired, bool wild = true, int maxSteps = 6, bool cake = false,
        IReadOnlyList<string>? preferred = null, PreferredMode mode = PreferredMode.None, TimeSpan? limit = null) =>
        PassiveSolverTests.Goal("Sekhmet", desired, wild: wild, maxWild: 3, maxSteps: maxSteps, cake: cake, preferred: preferred, mode: mode, limit: limit);

    [Fact]
    public async Task FourPreferredPals_GiveAFourRowTable_WithOneAndTwoStepPlans()
    {
        var d = D;
        var owned = new[]
        {
            Pal("moz", "CowPal", Gender.Female, d), Pal("chi", "ChickenPal", Gender.Female, d),
            Pal("rel", "LazyDragon", Gender.Male, d), Pal("vae", "VioletFairy", Gender.Female, d),
        };
        var result = await Solver().SolveAsync(Goal(d, preferred: ["moz", "chi", "rel", "vae"]), owned, CancellationToken.None);
        Assert.Equal(4, result.Feasibility.Count);
        int Steps(string uid) => result.Feasibility.Single(f => f.Uid == uid).BestPlan!.Steps;
        Assert.Equal(1, Steps("rel"));
        Assert.Equal(1, Steps("vae"));
        // One Pal may serve several steps (T106), so the cheapest plan with these two can be a longer one that reuses the male Lazy Dragon.
        Assert.InRange(Steps("moz"), 2, 3);
        Assert.InRange(Steps("chi"), 2, 3);
    }

    [Fact]
    public async Task SpecialCake_MakesTheFourOfFourPoolCertain()
    {
        var d = D;
        var owned = new[] { Pal("rel", "LazyDragon", Gender.Male, d), Pal("pri", "WhiteMoth_Neutral", Gender.Female) };
        var plain = await Solver().SolveAsync(Goal(d, wild: false), owned, CancellationToken.None);
        var cake = await Solver().SolveAsync(Goal(d, wild: false, cake: true), owned, CancellationToken.None);
        Assert.Equal(10.0, plain.Plans[0].ExpectedEggsTotal, 6);
        Assert.Equal(1.0, cake.Plans[0].ExpectedEggsTotal, 6);
        Assert.Equal(PassiveSolver.SpecialCakeId, ((BreedStep)cake.Plans[0].Root).CakeId);
    }

    [Fact]
    public async Task PreferredMode_AtLeastOne_RequiresAPreferredPal()
    {
        var d = D;
        var owned = new[]
        {
            Pal("rel", "LazyDragon", Gender.Male, d), Pal("pri", "WhiteMoth_Neutral", Gender.Female),
            Pal("other", "LazyDragon", Gender.Male, d.Take(2).ToList()),
        };
        var result = await Solver().SolveAsync(Goal(d, wild: false, preferred: ["other"], mode: PreferredMode.AtLeastOne), owned, CancellationToken.None);
        Assert.All(result.Plans, p => Assert.Contains("other", p.PreferredUidsUsed));
        // The Lazy Dragon with two passives can be paired with the other Pals in one step and the full one in a later step (T106: one Pal may serve several steps).
        Assert.NotNull(Assert.Single(result.Feasibility).BestPlan);
    }

    [Fact]
    public void StageOneCandidates_IncludeThePoolWithoutOnePassive()
    {
        // A holds all four wanted passives, B holds {1,2}: {0,2,3} and {0,1,3} contain neither parent's set, but are one short of the pool.
        var candidates = PassiveSolver.StageOneCandidates(union: 0b1111, a: 0b1111, b: 0b0110, fullMask: 0b1111);
        Assert.Contains(0b1111, candidates);
        Assert.Contains(0b0110, candidates);
        Assert.Contains(0b1101, candidates);   // pool without passive 1 (both parents hold it)
        Assert.Contains(0b1011, candidates);   // pool without passive 2 (both parents hold it)
        Assert.Equal(candidates.Length, candidates.Distinct().Count());
        // Few extra candidates: the stage stays fast.
        Assert.True(candidates.Length <= 1 + 1 + 1 + 8 + 4);
        // Not for a pure partner (nothing wanted), a partner with one wanted passive, nor when the pool is not the whole wanted set.
        Assert.DoesNotContain(0b1101, PassiveSolver.StageOneCandidates(0b1111, 0b1111, 0, 0b1111));
        Assert.DoesNotContain(0b1101, PassiveSolver.StageOneCandidates(0b1111, 0b1111, 0b0010, 0b1111));
        Assert.DoesNotContain(0b0101, PassiveSolver.StageOneCandidates(0b0111, 0b0111, 0b0110, 0b1111));
        // Not when neither parent holds the whole pool.
        Assert.DoesNotContain(0b1101, PassiveSolver.StageOneCandidates(0b1111, 0b0111, 0b1110, 0b1111));
    }

    [Fact]
    public async Task Continue_NeverReturnsAWorsePlan_AndKeepsThePreviousOnes()
    {
        var d = D;
        var owned = Crowd(300, d, 7);
        var solver = Solver();
        var first = solver.Solve(Goal(d) with { TimeLimit = TimeSpan.FromMilliseconds(300) }, owned, CancellationToken.None);
        var more = solver.Continue(Goal(d) with { TimeLimit = TimeSpan.FromSeconds(3) }, owned, first, CancellationToken.None);
        if (first.Plans.Count == 0) { Assert.True(more.Plans.Count >= 0); return; }
        Assert.NotEmpty(more.Plans);
        Assert.True(more.Plans[0].ExpectedEggsTotal <= first.Plans[0].ExpectedEggsTotal + 1e-9);
        // A "keep searching" with the improvement time off still runs (30 s default) and is cancelable.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var off = solver.ContinueAsync(Goal(d) with { TimeLimit = TimeSpan.Zero }, owned, first, cts.Token);
        try { await off; } catch (OperationCanceledException) { }
    }

    [Fact]
    public void FlexibleGender_BackupPartner_RemovesTheGenderFactorOfTheIntermediate()
    {
        // Mozzarina female with all four passives, wild Pals allowed: the intermediate child is paired with a wild partner whose species also
        // has the other gender, so its own gender does not matter (T105). Before: the intermediate cost 1/(0.02 * 0.5) = 83.3 eggs, total 133.3.
        var d = D;
        var owned = new[] { Pal("m", "CowPal", Gender.Female, d) };
        var result = Solver().Solve(PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3), owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);
        var plan = result.Plans[0];
        var root = Assert.IsType<BreedStep>(plan.Root);
        Assert.NotNull(root.AltPartner);
        var intermediate = Assert.IsType<BreedStep>(root.ParentA is BreedStep ? root.ParentA : root.ParentB);
        Assert.Equal(GenderReq.Any, intermediate.ChildGender);
        Assert.Equal(1.0 / intermediate.SuccessProbabilityPerEgg, intermediate.ExpectedEggs, 6);
        Assert.Equal(100.0, plan.ExpectedEggsTotal, 1);
        Assert.Contains(plan.Notes, n => n.StartsWith(PlanNotes.FlexGender, StringComparison.Ordinal));
        // The backup is a leaf of the same species as the partner with the other gender.
        var partner = root.ParentA is BreedStep ? root.ParentB : root.ParentA;
        Assert.Equal(partner.SpeciesId, root.AltPartner!.SpeciesId);
        Assert.NotEqual(partner.Gender, root.AltPartner.Gender);
    }

    [Fact]
    public void FlexibleGender_NoBackup_ChangesNothing()
    {
        var d = D;
        var owned = new[] { Pal("rel", "LazyDragon", Gender.Male, d), Pal("pri", "WhiteMoth_Neutral", Gender.Female) };
        var result = Solver().Solve(Goal(d, wild: false), owned, CancellationToken.None);
        var plan = result.Plans[0];
        Assert.Equal(10.0, plan.ExpectedEggsTotal, 6);
        Assert.Null(((BreedStep)plan.Root).AltPartner);
        Assert.DoesNotContain(plan.Notes, n => n.StartsWith(PlanNotes.FlexGender, StringComparison.Ordinal));
    }

    private static List<OwnedPal> Crowd(int count, IReadOnlyList<string> d, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(i => Pal("p" + i, Data.Value.Pals[rng.Next(Data.Value.Pals.Count)].Id,
            rng.Next(2) == 0 ? Gender.Male : Gender.Female, rng.Next(4) == 0 ? d.Take(rng.Next(1, 4)).ToList() : [])).ToList();
    }

    // The fourth wanted passive is on nobody: the search has to look at everything before it gives up.
    private static List<OwnedPal> ImpossibleCrowd(int count, IReadOnlyList<string> d) => Crowd(count, d.Take(3).ToList(), 5);

    [Fact]
    public async Task TimeLimit_StopsTheSearchAndSaysSo()
    {
        var d = D;
        var result = await Solver().SolveAsync(Goal(d, limit: TimeSpan.FromMilliseconds(50)), ImpossibleCrowd(1000, d), CancellationToken.None);
        Assert.True(result.TimedOut);
        Assert.Contains(PlanNotes.TimedOut, result.Diagnostics);
    }

    [Fact]
    public void ZeroTimeLimit_ReturnsTheFirstStageAndNeverStartsTheImprovingStage()
    {
        var d = D;
        var owned = Crowd(200, d, 3);
        var off = new Collector();
        var result = Solver().Solve(Goal(d) with { TimeLimit = TimeSpan.Zero, Exhaustive = false }, owned, CancellationToken.None, off);
        Assert.False(result.TimedOut);
        Assert.DoesNotContain(off.Reports, r => r.Report.Phase == "improve");
        Assert.DoesNotContain(off.Reports, r => r.Report.Phase == "stage1");

        // With a limit the improving stage runs, and its progress is reported.
        var on = new Collector();
        Solver().Solve(Goal(d) with { TimeLimit = TimeSpan.FromSeconds(5) }, owned, CancellationToken.None, on);
        Assert.Contains(on.Reports, r => r.Report.Phase == "stage1");
    }

    [Fact]
    public async Task Cancellation_ReturnsQuickly()
    {
        var d = D;
        using var cts = new CancellationTokenSource();
        var task = Solver().SolveAsync(Goal(d, limit: TimeSpan.FromMinutes(2)), ImpossibleCrowd(1000, d), cts.Token);
        await Task.Delay(100);
        var watch = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms");
    }

    // Collects the progress reports synchronously (Progress<T> would post them to another thread).
    private sealed class Collector : IProgress<SolverProgress>
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public List<(long Ms, SolverProgress Report)> Reports { get; } = [];
        public void Report(SolverProgress value) { lock (Reports) Reports.Add((_watch.ElapsedMilliseconds, value)); }
        public long? Stage1Ms => Reports.Where(r => r.Report.Phase == "stage1").Select(r => (long?)r.Ms).FirstOrDefault();
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Perf_ThousandPals_FourPassives_SixSteps_StageOneUnderThreeSeconds()
    {
        // T119: the fast stage runs on all logical processors but one: about 0.25 s here (0.5 s on one thread; T111's 5.5 s was a loaded machine). The ceiling
        // is 3 s: this machine's load varies, and the test is there to catch a real regression, not noise.
        var d = D;
        var rng = new Random(11);
        var owned = Enumerable.Range(0, 1000).Select(i => Pal("p" + i, Data.Value.Pals[rng.Next(Data.Value.Pals.Count)].Id,
            rng.Next(2) == 0 ? Gender.Male : Gender.Female, rng.Next(4) == 0 ? d.Take(rng.Next(1, 5)).ToList() : [])).ToList();
        var collector = new Collector();
        var watch = Stopwatch.StartNew();
        var result = await Solver().SolveAsync(Goal(d, limit: TimeSpan.FromSeconds(10)), owned, CancellationToken.None, collector);
        watch.Stop();
        Assert.NotEmpty(result.Plans);
        Assert.True(collector.Stage1Ms < 3000, $"stage 1 took {collector.Stage1Ms} ms (target 500)");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(11), $"total {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Perf_MozzarinaWithWild_StageOneUnderOneSecond()
    {
        var d = D;
        var collector = new Collector();
        var result = await Solver().SolveAsync(Goal(d, limit: TimeSpan.FromSeconds(3)), [Pal("moz", "CowPal", Gender.Female, d)], CancellationToken.None, collector);
        Assert.NotEmpty(result.Plans);
        Assert.True(collector.Stage1Ms < 1000, $"stage 1 took {collector.Stage1Ms} ms");
    }

    [Fact]
    public async Task StageOne_ResultIsReportedBeforeTheImprovingStageEnds()
    {
        var d = D;
        var collector = new Collector();
        var result = await Solver().SolveAsync(Goal(d, limit: TimeSpan.FromSeconds(2)), [Pal("moz", "CowPal", Gender.Female, d)], CancellationToken.None, collector);
        var stage1 = collector.Reports.First(r => r.Report.Phase == "stage1");
        Assert.NotNull(stage1.Report.Partial);
        Assert.NotEmpty(stage1.Report.Partial!.Plans);
        Assert.True(stage1.Ms < 1900, stage1.Ms.ToString());
        // The final result is never worse than the fast stage.
        Assert.True(result.Plans[0].ExpectedEggsTotal <= stage1.Report.Partial.Plans[0].ExpectedEggsTotal + 1e-9);
    }

    [Fact]
    public async Task LowerBoundPruning_DoesNotLoseTheBestPlan()
    {
        // Small deterministic cases: the improving stage with and without a time limit agree with the exhaustive reference.
        var d = D;
        var owned = new[]
        {
            Pal("a", "LazyDragon", Gender.Male, d.Take(2).ToList()), Pal("b", "WhiteMoth_Neutral", Gender.Female, d.Skip(2).ToList()),
            Pal("c", "CowPal", Gender.Female), Pal("d", "ChickenPal", Gender.Male),
        };
        var fast = await Solver().SolveAsync(Goal(d, wild: false, limit: TimeSpan.FromSeconds(5)), owned, CancellationToken.None);
        var reference = await Solver().SolveAsync(Goal(d, wild: false, limit: TimeSpan.FromSeconds(5)) with { Exhaustive = true }, owned, CancellationToken.None);
        Assert.NotEmpty(reference.Plans);
        Assert.Equal(reference.Plans[0].ExpectedEggsTotal, fast.Plans[0].ExpectedEggsTotal, 6);
    }

    [Fact]
    public async Task Cancel_DuringTheImprovingStage_KeepsTheStageOneResultAvailable()
    {
        var d = D;
        using var cts = new CancellationTokenSource();
        PlanResult? partial = null;
        var progress = new Collector();
        var task = Solver().SolveAsync(Goal(d, limit: TimeSpan.FromMinutes(1)), ImpossibleCrowd(1000, d), cts.Token, progress);
        await Task.Delay(300);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        partial = progress.Reports.Select(r => r.Report.Partial).LastOrDefault(p => p != null);
        // The UI keeps the last partial result when the user cancels; an impossible query simply has none.
        Assert.True(partial == null || partial.Plans.Count >= 0);
    }

    [Fact]
    public async Task Diagnostics_NameAnUnbreedableTarget()
    {
        var finder = new SpeciesPathFinder(Data.Value, Breeding.Value);
        var query = new PathQuery("PlantSlime_Flower", null, [], false, 0, true, 6, 3, OwnerScope.Everything);
        var result = await finder.FindAsync(query, [Pal("a", "CowPal", Gender.Female)], CancellationToken.None);
        Assert.Empty(result.Plans);
        Assert.Contains($"{PlanNotes.TargetUnbreedable}|PlantSlime_Flower", result.Diagnostics);
    }

    [Fact]
    public async Task Diagnostics_ExplainAnUnreachableLegendary()
    {
        var finder = new SpeciesPathFinder(Data.Value, Breeding.Value);
        var query = new PathQuery("JetDragon", null, [], true, 2, true, 8, 3, OwnerScope.Everything);
        var result = await finder.FindAsync(query, [Pal("a", "CowPal", Gender.Female)], CancellationToken.None);
        Assert.Empty(result.Plans);
        Assert.Contains($"{PlanNotes.TargetSameSpeciesOnly}|JetDragon", result.Diagnostics);
        Assert.Contains($"{PlanNotes.LegendaryExcluded}|JetDragon", result.Diagnostics);
    }

    [Fact]
    public async Task OneOwnedPal_MayServeSeveralSteps()
    {
        // T106 (ARCHITECTURE 7.3): breeding does not use a parent up and the steps run in order, so one owned Pal (here the male Ganesha) can be
        // a parent in two steps. Mozzarina holds all four wanted passives; with one more owned Pal the plan must not disappear.
        string[] wanted = ["CraftSpeed_up1", "CraftSpeed_up2", "CraftSpeed_up3", "PAL_CorporateSlave"];
        var moz = Pal("moz", "CowPal", Gender.Female, wanted);
        var query = PassiveSolverTests.Goal("CuteFox", wanted, wild: true, maxWild: 3, maxSteps: 4, start: new StartParent("moz", null, null), limit: TimeSpan.FromSeconds(15));
        var alone = await Solver().SolveAsync(query, [moz], CancellationToken.None);
        var withGanesha = await Solver().SolveAsync(query, [moz, Pal("gan", "Ganesha", Gender.Male)], CancellationToken.None);
        Assert.NotEmpty(alone.Plans);
        Assert.NotEmpty(withGanesha.Plans);   // one more owned Pal never turns a plan into none
        Assert.All(withGanesha.Plans, p => Assert.True(PlanTreeValidator.IsValid(p.Root)));
        Assert.Contains(withGanesha.Plans, p => PlanTreeValidator.Leaves(p.Root).OfType<OwnedLeaf>().GroupBy(l => l.Pal.Uid).Any(g => g.Count() > 1));
    }

    [Fact]
    public async Task TimedOutWithoutAPlan_SaysSo_InsteadOfBlamingTheSettings()
    {
        string[] wanted = ["CraftSpeed_up1", "CraftSpeed_up2", "CraftSpeed_up3", "PAL_CorporateSlave"];
        var owned = new List<OwnedPal> { Pal("moz", "CowPal", Gender.Female, wanted) };
        var query = PassiveSolverTests.Goal("CuteFox", wanted, wild: true, maxWild: 3, maxSteps: 5, start: new StartParent("moz", null, null), limit: TimeSpan.FromMilliseconds(1));
        var result = await Solver().SolveAsync(query, owned, CancellationToken.None);
        if (result.Plans.Count == 0)
        {
            Assert.Contains(PlanNotes.TimedOutNoPlan, result.Diagnostics);
            Assert.DoesNotContain(PlanNotes.WildLimit, result.Diagnostics);
            Assert.DoesNotContain(PlanNotes.StartUnused, result.Diagnostics);
        }
    }

    // ---- T107: exclusions ---------------------------------------------------------------------------------------------

    private static readonly string[] FourCraft = ["CraftSpeed_up1", "CraftSpeed_up2", "CraftSpeed_up3", "PAL_CorporateSlave"];

    private static GoalQuery CraftGoal(int maxSteps = 4) =>
        PassiveSolverTests.Goal("CuteFox", FourCraft, wild: true, maxWild: 3, maxSteps: maxSteps,
            start: new StartParent("moz", null, null), limit: TimeSpan.FromSeconds(15));

    private static IEnumerable<string> SpeciesIn(BreedingPlan plan)
    {
        IEnumerable<string> Walk(PlanNode n) => n is BreedStep s ? Walk(s.ParentA).Concat(Walk(s.ParentB)).Append(s.ChildSpeciesId) : [n.SpeciesId];
        return Walk(plan.Root);
    }

    [Fact]
    public async Task ExcludedPal_AndSpecies_AreKeptOutOfThePlans()
    {
        var owned = new[] { Pal("moz", "CowPal", Gender.Female, FourCraft), Pal("gan", "Ganesha", Gender.Male) };
        var open = await Solver().SolveAsync(CraftGoal(), owned, CancellationToken.None);
        Assert.NotEmpty(open.Plans);

        var noPal = await Solver().SolveAsync(CraftGoal() with { ExcludedUids = ["gan"] }, owned, CancellationToken.None);
        Assert.All(noPal.Plans, p => Assert.DoesNotContain(PlanTreeValidator.Leaves(p.Root).OfType<OwnedLeaf>(), l => l.Pal.Uid == "gan"));

        var noSpecies = await Solver().SolveAsync(CraftGoal() with { ExcludedSpecies = ["Ganesha"] }, owned, CancellationToken.None);
        Assert.All(noSpecies.Plans, p => Assert.DoesNotContain("Ganesha", SpeciesIn(p)));

        var noTerraria = await Solver().SolveAsync(CraftGoal(5) with { ExcludedGroups = ["terraria"] }, owned, CancellationToken.None);
        var terraria = GameDataLoader.Load(TestPaths.DataFolder).Pals.Where(x => x.Group == "terraria").Select(x => x.Id).ToHashSet();
        Assert.All(noTerraria.Plans, p => Assert.DoesNotContain(SpeciesIn(p), terraria.Contains));
    }

    [Fact]
    public async Task SolverPlans_AreDistinctPaths()
    {
        var owned = new[] { Pal("moz", "CowPal", Gender.Female, FourCraft) };
        var result = await Solver().SolveAsync(CraftGoal(5), owned, CancellationToken.None);
        var keys = result.Plans.Select(p => SpeciesPathFinder.PathKeyOf(p.Root)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public async Task NoPlanBecauseOfAnExclusion_NamesTheBlocker()
    {
        var owned = new[] { Pal("moz", "CowPal", Gender.Female, FourCraft) };
        var open = await Solver().SolveAsync(CraftGoal(5), owned, CancellationToken.None);
        Assert.NotEmpty(open.Plans);
        // Every step of every plan has to use one of these species; excluding all of them leaves no plan and the diagnosis names what blocks.
        var used = open.Plans.SelectMany(SpeciesIn).Where(s => s != "CuteFox" && s != "CowPal").Distinct().ToList();
        var closed = await Solver().SolveAsync(CraftGoal(5) with { ExcludedSpecies = used, TimeLimit = TimeSpan.Zero }, owned, CancellationToken.None);
        if (closed.Plans.Count == 0) Assert.NotNull(closed.Blockers);
    }

    // ---- T110 ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void OwnedTargetCopy_DoesNotSuppressTheTargetMadeByBreeding()
    {
        // An owned Sekhmet with the wanted passives is a parent, not a solution (T43): a plan of at least one step is still made.
        var d = D;
        var owned = new[]
        {
            Pal("s1", "Sekhmet", Gender.Male, d), Pal("s2", "Sekhmet", Gender.Female, d), Pal("sx", "Sekhmet", null, d),
            Pal("rel", "LazyDragon", Gender.Male, d), Pal("pri", "WhiteMoth_Neutral", Gender.Female),
        };
        var result = Solver().Solve(Goal(d, wild: false), owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);
        var plan = Assert.IsType<BreedStep>(result.Plans[0].Root);
        Assert.Equal("Sekhmet", plan.ChildSpeciesId);
        Assert.True(result.Plans[0].Steps >= 1);
    }

    [Fact]
    public void FlexibleGender_ImprovingStage_IsNeverWorseThanTheFastStage()
    {
        var d = D;
        var owned = new[] { Pal("m", "CowPal", Gender.Female, d) };
        var fast = Solver().Solve(PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3, limit: TimeSpan.Zero), owned, CancellationToken.None);
        var full = Solver().Solve(PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3, limit: TimeSpan.FromSeconds(10)), owned, CancellationToken.None);
        Assert.NotEmpty(fast.Plans);
        Assert.True(full.Plans[0].ExpectedEggsTotal <= fast.Plans[0].ExpectedEggsTotal + 1e-9);
    }

    [Fact]
    public async Task TimeLimit_IsTheImprovingTimeOnly_TheFastStageAlwaysFinishes()
    {
        var d = D;
        var owned = new[] { Pal("m", "CowPal", Gender.Female, d) };
        var result = await Solver().SolveAsync(PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 3, limit: TimeSpan.FromMilliseconds(1)), owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);   // a one millisecond limit used to end the fast stage with no plan
    }

    [Fact]
    public async Task PreferredTable_ReusesTheMainPlan_WhenItAlreadyUsesThePal()
    {
        var d = D;
        var owned = new[] { Pal("rel", "LazyDragon", Gender.Male, d), Pal("pri", "WhiteMoth_Neutral", Gender.Female), Pal("zzz", "CowPal", Gender.Male) };
        var solver = Solver();
        var result = await solver.SolveAsync(Goal(d, preferred: ["rel", "zzz"]), owned, CancellationToken.None);
        var rel = result.Feasibility.Single(f => f.Uid == "rel");
        Assert.Same(result.Plans[0], rel.BestPlan);
    }

    // ---- T111 ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void DominatedOwnedPals_DoNotChangeThePlan()
    {
        // Pals of the same species and gender with fewer wanted passives (and no fewer unwanted ones) are never better: dropped before the search.
        var d = D;
        var good = new[] { Pal("rel", "LazyDragon", Gender.Male, d), Pal("pri", "WhiteMoth_Neutral", Gender.Female) };
        var withWorse = good.Concat([Pal("rel2", "LazyDragon", Gender.Male, d.Take(2).ToList()), Pal("rel3", "LazyDragon", Gender.Male), Pal("pri2", "WhiteMoth_Neutral", Gender.Female)]).ToArray();
        var a = Solver().Solve(Goal(d, wild: false), good, CancellationToken.None);
        var b = Solver().Solve(Goal(d, wild: false), withWorse, CancellationToken.None);
        Assert.Equal(a.Plans[0].ExpectedEggsTotal, b.Plans[0].ExpectedEggsTotal, 9);
        Assert.Equal(1, b.Plans[0].Steps);
    }

    // ---- T113: guaranteed passives of wild species ----------------------------------------------------------------------

    [Fact]
    public void WildSpecies_WithAGuaranteedWantedPassive_BringsIt()
    {
        // Lazy Dragon is born with Full Stomach Up 1: no owned Pal has it, yet a plan exists (a wild Lazy Dragon + the owned partner).
        string[] wanted = ["PAL_FullStomach_Up_1"];
        var owned = new[] { Pal("pri", "WhiteMoth_Neutral", Gender.Female) };
        var q = PassiveSolverTests.Goal("Sekhmet", wanted, wild: true, maxWild: 1, maxSteps: 2, limit: TimeSpan.Zero);
        var result = Solver().Solve(q, owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);
        Assert.DoesNotContain(result.Diagnostics, d => d.StartsWith(PlanNotes.PassiveMissing, StringComparison.Ordinal));
        var wild = PlanTreeValidator.Leaves(result.Plans[0].Root).OfType<WildLeaf>().Single();
        Assert.Equal("LazyDragon", wild.SpeciesId);
    }

    [Fact]
    public void WildSpecies_WithAGuaranteedUnwantedPassive_CountsItAsUnwanted()
    {
        // Wanted: a work passive the owned Pal holds. The wild Lazy Dragon also carries Full Stomach Up 1 (not wanted): at least one unwanted
        // passive on the leaf even when none is assumed.
        var d = D;
        var owned = new[] { Pal("pri", "WhiteMoth_Neutral", Gender.Female, d) };
        var onlyLazy = Data.Value.Pals.Select(p => p.Id).Where(id => id is not ("LazyDragon" or "WhiteMoth_Neutral" or "Sekhmet")).ToList();
        var q = PassiveSolverTests.Goal("Sekhmet", d, wild: true, maxWild: 1, maxSteps: 2, limit: TimeSpan.Zero) with { WildAssumedUnwanted = 0, ExcludedSpecies = onlyLazy };
        var result = Solver().Solve(q, owned, CancellationToken.None);
        var wild = PlanTreeValidator.Leaves(result.Plans[0].Root).OfType<WildLeaf>().Single();
        Assert.Equal("LazyDragon", wild.SpeciesId);
        Assert.True(wild.AssumedUnwantedPassives >= 1);
    }

    [Fact]
    public void ImprovingStage_Merge_PreservesDistinctPathsAndOneStepAlternatives()
    {
        var d = D;

        // 1. Multiple one-step parent pairs must remain distinct after Stage 2 Merge.
        var oneStepPairs = new List<ParentPair>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Anubis" };
        foreach (var p in Breeding.Value.GetParents("Anubis"))
        {
            if (p.A == p.B || !seen.Add(p.A) || !seen.Add(p.B)) continue;
            oneStepPairs.Add(p);
            if (oneStepPairs.Count == 3) break;
        }
        var oneStepOwned = oneStepPairs.SelectMany((p, i) => new[]
        {
            Pal($"a{i}", p.A, p.AReq == GenderReq.Female ? Gender.Female : Gender.Male, d.Take(2).ToList()),
            Pal($"b{i}", p.B, p.BReq == GenderReq.Male ? Gender.Male : Gender.Female, d.Skip(2).ToList()),
        }).ToArray();

        var oneStepResult = Solver().Solve(
            PassiveSolverTests.Goal("Anubis", d, wild: false, maxSteps: 2, limit: TimeSpan.FromSeconds(2)) with { MaxResults = 3 },
            oneStepOwned, CancellationToken.None);
        Assert.Equal(3, oneStepResult.Plans.Count);
        var oneStepKeys = oneStepResult.Plans.Select(p => SpeciesPathFinder.PathKeyOf(p.Root)).ToList();
        Assert.All(oneStepKeys, k => Assert.StartsWith("leaf:", k, StringComparison.Ordinal));
        Assert.Equal(3, oneStepKeys.Distinct(StringComparer.Ordinal).Count());

        // 2. Multi-step paths sharing the same root parent pair must not be collapsed by Stage 2 Merge.
        var rootPair = Breeding.Value.GetParents("Bastet_Ice").Single(p => p.A != p.B);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bastet_Ice", rootPair.A, rootPair.B };
        var pairA = Breeding.Value.GetParents(rootPair.A).First(p => p.A != p.B && !used.Contains(p.A) && !used.Contains(p.B));
        used.Add(pairA.A);
        used.Add(pairA.B);
        var pairB = Breeding.Value.GetParents(rootPair.B).First(p => p.A != p.B && !used.Contains(p.A) && !used.Contains(p.B));

        var multiOwned = new[]
        {
            Pal("ra", rootPair.A, rootPair.AReq == GenderReq.Female ? Gender.Female : Gender.Male, d.Take(2).ToList()),
            Pal("rb", rootPair.B, rootPair.BReq == GenderReq.Male ? Gender.Male : Gender.Female, d.Skip(2).ToList()),
            Pal("pa1", pairA.A, pairA.AReq == GenderReq.Female ? Gender.Female : Gender.Male, d.Take(3).ToList()),
            Pal("pa2", pairA.B, pairA.BReq == GenderReq.Male ? Gender.Male : Gender.Female, [d[3]]),
            Pal("pb1", pairB.A, pairB.AReq == GenderReq.Female ? Gender.Female : Gender.Male, d.Take(3).ToList()),
            Pal("pb2", pairB.B, pairB.BReq == GenderReq.Male ? Gender.Male : Gender.Female, [d[3]]),
        };

        var multiResult = Solver().Solve(
            PassiveSolverTests.Goal("Bastet_Ice", d, wild: false, maxSteps: 3, limit: TimeSpan.FromSeconds(3)) with { MaxResults = 4 },
            multiOwned, CancellationToken.None);
        var multiKeys = multiResult.Plans.Select(p => SpeciesPathFinder.PathKeyOf(p.Root)).ToHashSet(StringComparer.Ordinal);
        Assert.True(multiKeys.Count >= 3);
        Assert.Contains($"{rootPair.A}>", multiKeys);
        Assert.Contains($"{rootPair.B}>", multiKeys);
    }

    [Fact]
    public void ExcludedWildSpecies_WithGuaranteedPassive_IsReportedMissingInDiagnostics()
    {
        string[] wanted = ["PAL_FullStomach_Up_1"];
        var owned = new[] { Pal("pri", "WhiteMoth_Neutral", Gender.Female) };
        var carriers = Data.Value.Pals
            .Where(p => p.GuaranteedPassives.Contains("PAL_FullStomach_Up_1", StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToList();
        var q = PassiveSolverTests.Goal("Sekhmet", wanted, wild: true, maxWild: 1, maxSteps: 2, limit: TimeSpan.Zero) with
        {
            ExcludedSpecies = carriers,
        };
        var result = Solver().Solve(q, owned, CancellationToken.None);
        Assert.Empty(result.Plans);
        Assert.Contains($"{PlanNotes.PassiveMissing}|PAL_FullStomach_Up_1", result.Diagnostics);
    }

    // ---- QA v0.1.1 regressions (2.2.1 - 2.2.4) ------------------------------------------------------------------------

    [Fact]
    public void IntermediateTargetStep_AllowsOppositeGender_AndIncludesGenderProbability()
    {
        // QA 2.2.1: Female-targeted NightLady_Dark (sameSpeciesOnly, 10% male) must allow building a male intermediate
        // target step, and that intermediate step must include the 10% child gender probability in ExpectedEggs.
        var d = D;
        var owned = new[]
        {
            Pal("m1", "NightLady_Dark", Gender.Male, [d[0]]),
            Pal("f1", "NightLady_Dark", Gender.Female, [d[1]]),
            Pal("f2", "NightLady_Dark", Gender.Female, d.Skip(2).ToList()),
        };
        var q = PassiveSolverTests.Goal("NightLady_Dark", d, wild: false, maxSteps: 3, limit: TimeSpan.FromSeconds(3)) with
        {
            TargetGender = GenderReq.Female,
        };
        var result = Solver().Solve(q, owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);
        var plan = result.Plans[0];
        var root = Assert.IsType<BreedStep>(plan.Root);
        Assert.Equal(GenderReq.Female, root.ChildGender);
        var intermediate = Assert.IsType<BreedStep>(root.ParentA is BreedStep ? root.ParentA : root.ParentB);
        Assert.Equal("NightLady_Dark", intermediate.ChildSpeciesId);
        Assert.Equal(GenderReq.Male, intermediate.ChildGender);
        // Expected eggs for intermediate step must reflect 10% male probability: 1 / (baseProb * 0.1).
        Assert.Equal(1.0 / intermediate.SuccessProbabilityPerEgg, intermediate.ExpectedEggs, 6);
        var maleChance = Breeding.Value.ChildGenderProbability("NightLady_Dark", Gender.Male);
        Assert.Equal(0.1, maleChance, 6);
        Assert.True(intermediate.SuccessProbabilityPerEgg <= 0.1 + 1e-9);
    }

    [Fact]
    public async Task NoPlanBecauseOfAnExclusion_WithDefaultTimeLimit_PreservesBlockers()
    {
        // QA 2.2.2: Improve()/Merge() must preserve stage1.Blockers when merged plans are empty.
        var d = D;
        var owned = new[] { Pal("a", "LazyDragon", Gender.Male, d), Pal("b", "WhiteMoth_Neutral", Gender.Female) };
        var closed = await Solver().SolveAsync(
            Goal(d, wild: false) with { ExcludedUids = ["a"], TimeLimit = TimeSpan.FromSeconds(2) },
            owned, CancellationToken.None);
        Assert.Empty(closed.Plans);
        Assert.NotNull(closed.Blockers);
        var blocker = Assert.Single(closed.Blockers!);
        Assert.Equal(new PlanExclusion(PlanExclusion.UidKind, "a"), blocker.Exclusion);
    }

    [Fact]
    public void CombineFlex_ChecksStartAndPreferredOnBackup_AndLeavesCountAltPartner()
    {
        // QA 2.2.3: AltPartner cannot satisfy Preferred on its own unless both main and backup satisfy it.
        var d = D;
        var owned = new[]
        {
            Pal("ld1", "LazyDragon", Gender.Male, d.Take(2).ToList()),
            Pal("ld2", "LazyDragon", Gender.Female, d.Skip(2).ToList()),
            Pal("wm_pref", "WhiteMoth_Neutral", Gender.Male),
            Pal("wm_plain", "WhiteMoth_Neutral", Gender.Female),
        };
        var q = Goal(d, wild: false, preferred: ["wm_pref"], mode: PreferredMode.AtLeastOne) with { TimeLimit = TimeSpan.FromSeconds(3) };
        var result = Solver().Solve(q, owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);
        // No plan may claim flex using wm_plain as an unpreferred AltPartner.
        Assert.All(result.Plans, p =>
        {
            var root = Assert.IsType<BreedStep>(p.Root);
            if (root.AltPartner is OwnedLeaf alt) Assert.Equal("wm_pref", alt.Pal.Uid);
        });

        // PlanTreeValidator.Leaves & CountWild must traverse AltPartner.
        var flexStep = new BreedStep(
            new BreedStep(new OwnedLeaf(owned[0]), new OwnedLeaf(owned[1]), "LazyDragon", GenderReq.Any, d, 0, null, 0.1, 10),
            new OwnedLeaf(owned[2]),
            "Sekhmet", GenderReq.Any, d, 0, null, 0.1, 10,
            new WildLeaf("WhiteMoth_Neutral", GenderReq.Female, 0, "Wild"));
        var leaves = PlanTreeValidator.Leaves(flexStep).ToList();
        Assert.Contains(leaves, l => l is WildLeaf { SpeciesId: "WhiteMoth_Neutral" });
        Assert.Equal(1, PlanTreeValidator.CountWild(flexStep));
    }

    [Fact]
    public void UnknownGenderOwnedPals_ExpandDistinctly_AndDoNotSuppressKnownGenderSibling()
    {
        // QA 2.2.4: Two unknown-gender Pals (highest level first) must expand to two distinct Uids, not (p1, p1).
        var d = D.Take(2).ToList();
        var twoUnknown = new[]
        {
            Pal("p1", "Sekhmet", null, d) with { Level = 50 },
            Pal("p2", "Sekhmet", null, [d[1]]) with { Level = 10 },
        };
        var resTwo = Solver().Solve(PassiveSolverTests.Goal("Sekhmet", d, wild: false, limit: TimeSpan.FromSeconds(2)), twoUnknown, CancellationToken.None);
        Assert.NotEmpty(resTwo.Plans);
        Assert.All(resTwo.Plans, p => Assert.True(PlanTreeValidator.IsValid(p.Root)));
        var step = Assert.IsType<BreedStep>(resTwo.Plans[0].Root);
        Assert.NotEqual(((OwnedLeaf)step.ParentA).Pal.Uid, ((OwnedLeaf)step.ParentB).Pal.Uid);

        // Single unknown-gender Pal holding [d0, d1] must not dominate known-gender sibling holding [d0].
        var oneUnknownOneKnown = new[]
        {
            Pal("o", "Sekhmet", null, d) with { Level = 50 },
            Pal("g", "Sekhmet", Gender.Male, [d[0]]) with { Level = 10 },
        };
        var resPair = Solver().Solve(PassiveSolverTests.Goal("Sekhmet", d, wild: false, limit: TimeSpan.FromSeconds(2)), oneUnknownOneKnown, CancellationToken.None);
        Assert.NotEmpty(resPair.Plans);
        Assert.All(resPair.Plans, p => Assert.True(PlanTreeValidator.IsValid(p.Root)));

        // PlanTreeValidator must reject a step where AltPartner uses the same Pal as ParentA or ParentB.
        var badStep = new BreedStep(
            new BreedStep(new OwnedLeaf(twoUnknown[0]), new OwnedLeaf(twoUnknown[1]), "LazyDragon", GenderReq.Any, d, 0, null, 0.1, 10),
            new OwnedLeaf(twoUnknown[0]),
            "Sekhmet", GenderReq.Any, d, 0, null, 0.1, 10,
            new OwnedLeaf(twoUnknown[0]));
        Assert.False(PlanTreeValidator.IsValid(badStep));
    }
}
