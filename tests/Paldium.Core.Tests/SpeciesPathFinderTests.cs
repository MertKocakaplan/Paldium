using System.Diagnostics;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;

namespace Paldium.Core.Tests;

// CPU heavy or timing sensitive tests run one after another (the bundle load test has a one second limit).
[Collection("Heavy")]
public class SpeciesPathFinderTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static readonly Lazy<BreedingCalculator> Breeding = new(() => BreedingCalculator.Load(Data.Value, TestPaths.DataFolder));
    private static SpeciesPathFinder Finder() => new(Data.Value, Breeding.Value);

    private static OwnedPal Pal(string uid, string species, Gender? gender, int level = 10,
        LocationKind location = LocationKind.Palbox, bool excluded = false) =>
        new(uid, null, species, gender, level, 1, [], null, [], [], null, null, new PalLocation(location, null, null, null),
            PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, excluded);

    private static PathQuery Query(string target, StartParent? start = null, bool wild = false, int maxWild = 2, int maxSteps = 6,
        int maxResults = 5, IReadOnlyList<string>? preferred = null, bool excludeLegendary = true) =>
        new(target, start, preferred ?? [], wild, maxWild, excludeLegendary, maxSteps, maxResults, OwnerScope.Everything);

    [Fact]
    public async Task OneStep_FromTwoOwnedParents()
    {
        var owned = new[] { Pal("a", "LazyDragon", Gender.Male), Pal("b", "WhiteMoth_Neutral", Gender.Female) };
        var result = await Finder().FindAsync(Query("Sekhmet"), owned, CancellationToken.None);
        var plan = Assert.Single(result.Plans);
        Assert.Equal(1, plan.Steps);
        Assert.Equal(0, plan.WildCount);
        var step = Assert.IsType<BreedStep>(plan.Root);
        Assert.Equal("Sekhmet", step.ChildSpeciesId);
        Assert.All(new[] { step.ParentA, step.ParentB }, n => Assert.IsType<OwnedLeaf>(n));
        Assert.Equal(1.0, plan.ExpectedEggsTotal, 9);
    }

    [Fact]
    public async Task StartSpecies_IsUsedInTheFirstStep_WithWildOn()
    {
        var owned = new[] { Pal("m", "CowPal", Gender.Female) };
        var query = Query("Sekhmet", new StartParent(null, "CowPal", null), wild: true, maxWild: 2, maxSteps: 6);
        var result = await Finder().FindAsync(query, owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);
        var plan = result.Plans[0];
        Assert.True(plan.UsesStartParent);
        Assert.Contains(PlanTreeValidator.Leaves(plan.Root), l => l.SpeciesId == "CowPal");
        Assert.Equal(2, plan.Steps);
    }

    [Fact]
    public async Task WildOffAndUnreachable_GivesEmptyPlanWithDiagnostics()
    {
        var result = await Finder().FindAsync(Query("Sekhmet"), [Pal("x", "CowPal", Gender.Female)], CancellationToken.None);
        Assert.Empty(result.Plans);
        Assert.Contains(PlanNotes.NoPlan, result.Diagnostics);
        Assert.Contains(PlanNotes.WildOff, result.Diagnostics);
    }

    [Fact]
    public async Task OwnedTarget_DoesNotEndTheSearch_ItIsAnInfoNote_AndPlansHaveAtLeastOneStep()
    {
        var owned = new[] { Pal("s", "Sekhmet", Gender.Male), Pal("a", "LazyDragon", Gender.Male), Pal("b", "WhiteMoth_Neutral", Gender.Female) };
        var result = await Finder().FindAsync(Query("Sekhmet"), owned, CancellationToken.None);

        Assert.NotNull(result.OwnedTargets);
        Assert.Equal(["s"], result.OwnedTargets!.Select(p => p.Uid));
        Assert.NotEmpty(result.Plans);
        Assert.All(result.Plans, p => Assert.True(p.Steps >= 1));
        // The owned target is not one of the parents of the plan that makes a new one from the other two.
        Assert.DoesNotContain(PlanTreeValidator.Leaves(result.Plans[0].Root), l => l is OwnedLeaf { Pal.Uid: "s" });
    }

    [Fact]
    public async Task OnlyAnOwnedTarget_NoPlanButTheNote()
    {
        var result = await Finder().FindAsync(Query("Sekhmet"), [Pal("s", "Sekhmet", Gender.Male)], CancellationToken.None);
        Assert.Empty(result.Plans);
        Assert.Contains(PlanNotes.NoPlan, result.Diagnostics);
        Assert.Single(result.OwnedTargets!);
    }

    [Fact]
    public async Task OwnedMaleAndFemaleTarget_BreedTogether_InOneStep()
    {
        var owned = new[] { Pal("m", "Sekhmet", Gender.Male), Pal("f", "Sekhmet", Gender.Female) };
        var result = await Finder().FindAsync(Query("Sekhmet"), owned, CancellationToken.None);

        var plan = result.Plans[0];
        Assert.Equal(1, plan.Steps);
        var step = Assert.IsType<BreedStep>(plan.Root);
        Assert.Equal("Sekhmet", step.ChildSpeciesId);
        Assert.Equal(["f", "m"], PlanTreeValidator.Leaves(plan.Root).OfType<OwnedLeaf>().Select(l => l.Pal.Uid).Order().ToArray());
        Assert.Equal(2, result.OwnedTargets!.Count);
    }

    [Fact]
    public async Task TargetGender_IsPartOfTheLastStep()
    {
        var owned = new[] { Pal("a", "LazyDragon", Gender.Male), Pal("b", "WhiteMoth_Neutral", Gender.Female) };
        var any = await Finder().FindAsync(Query("Sekhmet"), owned, CancellationToken.None);
        var female = await Finder().FindAsync(Query("Sekhmet") with { TargetGender = GenderReq.Female }, owned, CancellationToken.None);

        var step = Assert.IsType<BreedStep>(female.Plans[0].Root);
        Assert.Equal(GenderReq.Female, step.ChildGender);
        var p = Breeding.Value.ChildGenderProbability("Sekhmet", Gender.Female);
        Assert.Equal(1.0 / p, female.Plans[0].ExpectedEggsTotal, 9);
        Assert.Equal(p, step.SuccessProbabilityPerEgg, 9);
        Assert.Equal(1.0, any.Plans[0].ExpectedEggsTotal, 9); // without a wanted gender nothing changes
        Assert.Equal(GenderReq.Any, Assert.IsType<BreedStep>(any.Plans[0].Root).ChildGender);
    }
    [Fact]
    public async Task ExcludedAndOutOfScopePals_AreNotUsed()
    {
        var owned = new[]
        {
            Pal("a", "LazyDragon", Gender.Male, excluded: true),
            Pal("b", "WhiteMoth_Neutral", Gender.Female),
        };
        var excluded = await Finder().FindAsync(Query("Sekhmet"), owned, CancellationToken.None);
        Assert.Empty(excluded.Plans);

        var included = await Finder().FindAsync(
            Query("Sekhmet") with { Scope = OwnerScope.Everything with { IncludeExcluded = true } }, owned, CancellationToken.None);
        Assert.NotEmpty(included.Plans);

        var scope = new OwnerScope(null, [LocationKind.Party]);
        var none = await Finder().FindAsync(Query("Sekhmet") with { Scope = scope }, owned, CancellationToken.None);
        Assert.Empty(none.Plans);
    }

    [Fact]
    public async Task SamePhysicalPal_IsNeverUsedTwice()
    {
        // One Pal of unknown gender cannot be both parents; with wild on, the plan takes a wild partner.
        var owned = new[] { Pal("only", "LazyDragon", null) };
        var result = await Finder().FindAsync(Query("Sekhmet", wild: true, maxWild: 3), owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);
        Assert.All(result.Plans, p => Assert.True(PlanTreeValidator.IsValid(p.Root)));
    }

    [Fact]
    public void Validator_RejectsADuplicateOwnedLeaf()
    {
        var pal = Pal("same", "LazyDragon", Gender.Male);
        var step = new BreedStep(new OwnedLeaf(pal), new OwnedLeaf(pal), "Sekhmet", GenderReq.Any, [], 0, null, 1, 1);
        Assert.False(PlanTreeValidator.IsValid(step));
        Assert.True(PlanTreeValidator.IsValid(new BreedStep(new OwnedLeaf(pal), new OwnedLeaf(Pal("other", "WhiteMoth_Neutral", Gender.Female)), "Sekhmet", GenderReq.Any, [], 0, null, 1, 1)));
    }

    [Fact]
    public async Task MaxResults_GivesDifferentRootRecipesSortedBySteps()
    {
        var owned = Data.Value.Pals.Take(80).SelectMany(p => new[]
        {
            Pal(p.Id + "-m", p.Id, Gender.Male), Pal(p.Id + "-f", p.Id, Gender.Female),
        }).ToList();
        var result = await Finder().FindAsync(Query("Sekhmet", maxSteps: 4, maxResults: 5), owned, CancellationToken.None);
        Assert.InRange(result.Plans.Count, 1, 5);
        var pairs = result.Plans.Select(p => (BreedStep)p.Root)
            .Select(s => string.Join("+", new[] { s.ParentA.SpeciesId, s.ParentB.SpeciesId }.Order(StringComparer.Ordinal))).ToList();
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
        Assert.Equal(result.Plans.OrderBy(p => p.Steps).Select(p => p.Steps), result.Plans.Select(p => p.Steps));
    }

    [Fact]
    public async Task PreferredPal_MustBeUsed()
    {
        var owned = new[]
        {
            Pal("a", "LazyDragon", Gender.Male), Pal("b", "WhiteMoth_Neutral", Gender.Female),
            Pal("c", "LazyDragon", Gender.Male, level: 50),
        };
        var result = await Finder().FindAsync(Query("Sekhmet", preferred: ["c"]), owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);
        Assert.All(result.Plans, p => Assert.Contains("c", p.PreferredUidsUsed));
        Assert.Null(Assert.Single(result.Feasibility).ReasonIfNone);
    }

    private static OwnedPal PalOf(string uid, string species, Gender gender, string owner, LocationKind location) =>
        Pal(uid, species, gender, location: location) with { Owner = new PalOwner(owner, null, null, null) };

    [Fact]
    public async Task GuildSharedLocation_CountsForMe_WhoeverOwnsThePal()
    {
        // The storage Pal belongs to the host; the player is somebody else. My Pals shows it as "used for planning" and so must the planner.
        var owned = new[]
        {
            PalOf("a", "LazyDragon", Gender.Male, "host", LocationKind.DimensionalStorage),
            PalOf("b", "WhiteMoth_Neutral", Gender.Female, "host", LocationKind.Base),
        };
        IReadOnlyList<LocationKind> kinds = [LocationKind.DimensionalStorage, LocationKind.Base, LocationKind.Palbox];
        var shared = new OwnerScope(["me"], kinds, OwnerFreeLocations: [LocationKind.Base, LocationKind.DimensionalStorage]);
        var notShared = new OwnerScope(["me"], kinds);

        var withGuild = await Finder().FindAsync(Query("Sekhmet") with { Scope = shared }, owned, CancellationToken.None);
        var without = await Finder().FindAsync(Query("Sekhmet") with { Scope = notShared }, owned, CancellationToken.None);

        Assert.NotEmpty(withGuild.Plans);
        Assert.Empty(without.Plans);
    }

    [Fact]
    public async Task PreferredPal_IsNotRequiredWhenThePreferredModeIsOff()
    {
        var owned = new[]
        {
            Pal("a", "LazyDragon", Gender.Male), Pal("b", "WhiteMoth_Neutral", Gender.Female),
            Pal("c", "CowPal", Gender.Male, level: 50), // cannot make Sekhmet within three steps (a longer chain exists since one Pal may serve several steps)
        };
        var required = await Finder().FindAsync(Query("Sekhmet", preferred: ["c"], maxSteps: 3), owned, CancellationToken.None);
        var optional = await Finder().FindAsync(Query("Sekhmet", preferred: ["c"], maxSteps: 3) with { PreferredMode = PreferredMode.None }, owned, CancellationToken.None);

        Assert.Empty(required.Plans);
        Assert.NotEmpty(optional.Plans);
        Assert.Equal(PlanNotes.NotUsable, Assert.Single(optional.Feasibility).ReasonIfNone);
    }

    [Fact]
    public async Task TwoPreferredPals_OfTheSameRootPair_AreBothUsable()
    {
        var owned = new[]
        {
            Pal("a", "LazyDragon", Gender.Male, level: 10), Pal("a2", "LazyDragon", Gender.Male, level: 40),
            Pal("b", "WhiteMoth_Neutral", Gender.Female),
        };
        var result = await Finder().FindAsync(Query("Sekhmet", preferred: ["a", "a2"]) with { PreferredMode = PreferredMode.None }, owned, CancellationToken.None);

        Assert.Equal(2, result.Feasibility.Count);
        Assert.All(result.Feasibility, f => Assert.Null(f.ReasonIfNone));
        Assert.All(result.Feasibility, f => Assert.NotNull(f.BestPlan));
    }

    [Fact]
    public async Task LegendaryWild_IsExcludedByDefault()
    {
        var withLegendary = await Finder().FindAsync(Query("Sekhmet", wild: true, maxWild: 4, maxSteps: 3, excludeLegendary: false), [], CancellationToken.None);
        var without = await Finder().FindAsync(Query("Sekhmet", wild: true, maxWild: 4, maxSteps: 3, excludeLegendary: true), [], CancellationToken.None);
        static IEnumerable<string> WildSpecies(PlanResult r) =>
            r.Plans.SelectMany(p => PlanTreeValidator.Leaves(p.Root)).OfType<WildLeaf>().Select(l => l.SpeciesId);
        Assert.All(WildSpecies(without), id => Assert.True(Data.Value.Pal(id).Rarity < LeafBuilder.LegendaryRarity));
        Assert.NotNull(withLegendary);
    }

    [Fact]
    public async Task WildLeaf_CarriesTheEasiestObtainMethod()
    {
        var result = await Finder().FindAsync(Query("Sekhmet", wild: true, maxWild: 2, maxSteps: 3), [], CancellationToken.None);
        var wild = result.Plans.SelectMany(p => PlanTreeValidator.Leaves(p.Root)).OfType<WildLeaf>().ToList();
        Assert.NotEmpty(wild);
        Assert.All(wild, l => Assert.Contains(l.ObtainMethod, new[] { "Wild", "Cage", "Merchant", "Fishing", "Dungeon", "AlphaBoss", "Imprisoned", "DungeonBoss", "Raid" }));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Perf_ThousandOwnedPals_EightSteps_UnderOneSecond()
    {
        var rng = new Random(7);
        var species = Data.Value.Pals;
        var owned = Enumerable.Range(0, 1000)
            .Select(i => Pal("p" + i, species[rng.Next(species.Count)].Id, rng.Next(2) == 0 ? Gender.Male : Gender.Female))
            .ToList();
        var finder = Finder();
        await finder.FindAsync(Query("Sekhmet", maxSteps: 8), owned, CancellationToken.None); // warm up
        var watch = Stopwatch.StartNew();
        var result = await finder.FindAsync(Query("Sekhmet", maxSteps: 8), owned, CancellationToken.None);
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
        Assert.NotNull(result);
    }

    // ---- T107: exclusions and distinct paths ---------------------------------------------------------------------------

    private static IEnumerable<string> SpeciesOf(BreedingPlan plan) =>
        PlanTreeValidator.Leaves(plan.Root).Select(l => l.SpeciesId)
            .Concat(Steps(plan.Root).Select(s => s.ChildSpeciesId)).Distinct();

    private static IEnumerable<BreedStep> Steps(PlanNode n) =>
        n is BreedStep s ? Steps(s.ParentA).Concat(Steps(s.ParentB)).Append(s) : [];

    [Fact]
    public void Plans_AreDistinctPaths_NotTheSamePathWithOtherPartners()
    {
        var q = Query("CuteFox", start: new StartParent(null, "CowPal", null), wild: true, maxWild: 3, maxSteps: 5, maxResults: 4);
        var plans = Finder().Find(q, [], CancellationToken.None).Plans;
        Assert.True(plans.Count >= 2);
        var paths = plans.Select(p => SpeciesPathFinder.PathKeyOf(p.Root)).ToList();
        Assert.Equal(paths.Count, paths.Distinct().Count());
    }

    [Fact]
    public void ExcludedGroup_KeepsItsSpeciesOutOfEveryPlan_ExceptTheStartPal()
    {
        var q = Query("CuteFox", start: new StartParent(null, "CowPal", null), wild: true, maxWild: 3, maxSteps: 5, maxResults: 4);
        var terraria = Data.Value.Pals.Where(p => p.Group == "terraria").Select(p => p.Id).ToHashSet();
        Assert.Equal(11, terraria.Count);
        var open = Finder().Find(q, [], CancellationToken.None).Plans;
        Assert.Contains(open, p => SpeciesOf(p).Any(terraria.Contains));
        var closed = Finder().Find(q with { ExcludedGroups = ["terraria"] }, [], CancellationToken.None).Plans;
        Assert.All(closed, p => Assert.DoesNotContain(SpeciesOf(p), terraria.Contains));
    }

    [Fact]
    public void ExcludedUid_IsNotUsed_ButTheStartPalIsExempt()
    {
        var owned = new[] { Pal("a", "LazyDragon", Gender.Male), Pal("b", "WhiteMoth_Neutral", Gender.Female) };
        var q = Query("Sekhmet");
        Assert.NotEmpty(Finder().Find(q, owned, CancellationToken.None).Plans);
        var without = Finder().Find(q with { ExcludedUids = ["a"] }, owned, CancellationToken.None);
        Assert.Empty(without.Plans);
        // Lifting that one exclusion gives a plan again: the diagnosis names it.
        var blocker = Assert.Single(without.Blockers!);
        Assert.Equal(new PlanExclusion(PlanExclusion.UidKind, "a"), blocker.Exclusion);
        Assert.Equal(1, blocker.Steps);
        // The start Pal is exempt from the exclusion.
        var start = Finder().Find(q with { ExcludedUids = ["a"], Start = new StartParent("a", null, null) }, owned, CancellationToken.None);
        Assert.NotEmpty(start.Plans);
    }

    [Fact]
    public void ExcludedSpecies_IsNotUsedAsAnIntermediateChild()
    {
        var q = Query("CuteFox", start: new StartParent(null, "CowPal", null), wild: true, maxWild: 3, maxSteps: 5, maxResults: 4);
        var plans = Finder().Find(q, [], CancellationToken.None).Plans;
        var middle = plans.SelectMany(p => Steps(p.Root)).Select(s => s.ChildSpeciesId).First(id => id != "CuteFox");
        var excluded = Finder().Find(q with { ExcludedSpecies = [middle] }, [], CancellationToken.None).Plans;
        Assert.All(excluded, p => Assert.DoesNotContain(middle, SpeciesOf(p)));
    }

    [Fact]
    public void OneOwnedPal_ServesSeveralSteps_InAPlanWithoutPassives()
    {
        // T110 (ARCHITECTURE 7.3): breeding does not use a parent up, so the same owned Pal can be a parent in two steps.
        var owned = new[] { Pal("a", "LazyDragon", Gender.Male), Pal("b", "WhiteMoth_Neutral", Gender.Female), Pal("c", "CowPal", Gender.Male, level: 50) };
        var plans = Finder().Find(Query("Sekhmet", preferred: ["c"], maxSteps: 6), owned, CancellationToken.None).Plans;
        var plan = Assert.Single(plans);
        Assert.Contains(PlanTreeValidator.Leaves(plan.Root).OfType<OwnedLeaf>().GroupBy(l => l.Pal.Uid), g => g.Count() > 1);
        Assert.True(PlanTreeValidator.IsValid(plan.Root));
    }

    [Fact]
    public void ManyExclusions_WithoutAPlan_AreDiagnosedInBoundedTime()
    {
        // The blockers are looked for one level only: lifting an exclusion must not start another search for blockers (it did, exponentially).
        var owned = new[] { Pal("a", "LazyDragon", Gender.Male), Pal("b", "WhiteMoth_Neutral", Gender.Female) };
        var uids = Enumerable.Range(0, 14).Select(i => "x" + i).Append("a").ToList();
        var sw = Stopwatch.StartNew();
        var result = Finder().Find(Query("Sekhmet") with { ExcludedUids = uids, ExcludedSpecies = ["CowPal", "Nox", "Anubis", "Kitsunebi", "Lamball", "WizardOwl"] }, owned, CancellationToken.None);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Empty(result.Plans);
        Assert.Contains(result.Blockers!, b => b.Exclusion.Id == "a");
    }

    [Fact]
    public void OneStepPlans_WithDifferentParentPairs_HaveDistinctPathKeys_AndAreAllReturned()
    {
        var pairs = new List<ParentPair>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Anubis" };
        foreach (var p in Breeding.Value.GetParents("Anubis"))
        {
            if (p.A == p.B || !seen.Add(p.A) || !seen.Add(p.B)) continue;
            pairs.Add(p);
            if (pairs.Count == 4) break;
        }
        Assert.Equal(4, pairs.Count);

        var owned = pairs.SelectMany((p, i) => new[]
        {
            Pal($"a{i}", p.A, p.AReq == GenderReq.Female ? Gender.Female : Gender.Male),
            Pal($"b{i}", p.B, p.BReq == GenderReq.Male ? Gender.Male : Gender.Female),
        }).ToList();

        var result = Finder().Find(Query("Anubis", maxResults: 4), owned, CancellationToken.None);
        Assert.Equal(4, result.Plans.Count);
        Assert.All(result.Plans, p => Assert.Equal(1, p.Steps));
        var keys = result.Plans.Select(p => SpeciesPathFinder.PathKeyOf(p.Root)).ToList();
        Assert.All(keys, k => Assert.StartsWith("leaf:", k, StringComparison.Ordinal));
        Assert.Equal(4, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PathKeyOf_BranchingTree_IsCanonicalRegardlessOfParentOrder()
    {
        var leaf1 = new OwnedLeaf(Pal("l1", "CowPal", Gender.Male));
        var leaf2 = new OwnedLeaf(Pal("l2", "SheepBall", Gender.Female));
        var leaf3 = new OwnedLeaf(Pal("l3", "ChickenPal", Gender.Male));
        var leaf4 = new OwnedLeaf(Pal("l4", "Ganesha", Gender.Female));

        var branchA = new BreedStep(leaf1, leaf2, "WizardOwl", GenderReq.Male, [], 0, null, 0.5, 2);
        var branchB = new BreedStep(leaf3, leaf4, "ElecCat", GenderReq.Female, [], 0, null, 0.5, 2);
        var root1 = new BreedStep(branchA, branchB, "CuteFox", GenderReq.Any, [], 0, null, 1, 1);
        var root2 = new BreedStep(branchB, branchA, "CuteFox", GenderReq.Any, [], 0, null, 1, 1);

        Assert.Equal(SpeciesPathFinder.PathKeyOf(root1), SpeciesPathFinder.PathKeyOf(root2));
    }

    [Fact]
    public void MultiStepPlans_SharingTheSameRootPair_AreBothKeptInAlternatives()
    {
        var rootPair = Breeding.Value.GetParents("Bastet_Ice").Single(p => p.A != p.B);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Bastet_Ice", rootPair.A, rootPair.B };
        var pairA = Breeding.Value.GetParents(rootPair.A).First(p => p.A != p.B && !used.Contains(p.A) && !used.Contains(p.B));
        used.Add(pairA.A);
        used.Add(pairA.B);
        var pairB = Breeding.Value.GetParents(rootPair.B).First(p => p.A != p.B && !used.Contains(p.A) && !used.Contains(p.B));

        var owned = new[]
        {
            Pal("ra", rootPair.A, Gender.Male),
            Pal("rb", rootPair.B, Gender.Male),
            Pal("pa1", pairA.A, pairA.AReq == GenderReq.Female ? Gender.Female : Gender.Male),
            Pal("pa2", pairA.B, pairA.BReq == GenderReq.Male ? Gender.Male : Gender.Female),
            Pal("pb1", pairB.A, pairB.AReq == GenderReq.Female ? Gender.Female : Gender.Male),
            Pal("pb2", pairB.B, pairB.BReq == GenderReq.Male ? Gender.Male : Gender.Female),
        };

        var result = Finder().Find(Query("Bastet_Ice", maxSteps: 2, maxResults: 5), owned, CancellationToken.None);
        var keys = result.Plans.Select(p => SpeciesPathFinder.PathKeyOf(p.Root)).ToHashSet(StringComparer.Ordinal);
        Assert.Contains($"{rootPair.A}>", keys);
        Assert.Contains($"{rootPair.B}>", keys);
    }
}
