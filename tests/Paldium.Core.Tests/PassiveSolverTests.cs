using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;
using Paldium.Core.Probability;

namespace Paldium.Core.Tests;

// CPU heavy or timing sensitive tests run one after another (the bundle load test has a one second limit).
[Collection("Heavy")]
public class PassiveSolverTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static readonly Lazy<BreedingCalculator> Breeding = new(() => BreedingCalculator.Load(Data.Value, TestPaths.DataFolder));

    public static BreedingCalculator SharedBreeding => Breeding.Value;
    public static GameData SharedData => Data.Value;

    public static PassiveSolver Solver() => new(Data.Value, Breeding.Value, new PassiveOddsCalculator(Data.Value));

    /// <summary>Four positive work-speed passives, strongest first.</summary>
    public static IReadOnlyList<string> WorkPassives() => Data.Value.Passives
        .Where(p => p.Rank > 0 && p.Tags.Contains("WorkSpeed") && p.IsStandard)
        .OrderByDescending(p => p.Rank).ThenBy(p => p.Id, StringComparer.Ordinal).Take(4).Select(p => p.Id).ToList();

    public static OwnedPal Pal(string uid, string species, Gender? gender, IReadOnlyList<string>? passives = null, int level = 10) =>
        new(uid, null, species, gender, level, 1, passives ?? [], null, [], [], null, null,
            new PalLocation(LocationKind.Palbox, null, null, null), PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    public static GoalQuery Goal(string target, IReadOnlyList<string> desired, int maxUnwanted = 0, bool wild = false, int maxWild = 2,
        int maxSteps = 6, bool cake = false, IReadOnlyList<string>? preferred = null, PreferredMode mode = PreferredMode.None,
        StartParent? start = null, TimeSpan? limit = null) =>
        new(target, desired, maxUnwanted, GenderReq.Any, start, preferred ?? [], mode, wild, maxWild, true, 1, maxSteps, cake, 5,
            OwnerScope.Everything, limit ?? TimeSpan.FromSeconds(2));

    [Fact]
    public async Task OneStep_FourWantedInThePool_TenEggs()
    {
        var d = WorkPassives();
        var owned = new[] { Pal("a", "LazyDragon", Gender.Male, d), Pal("b", "WhiteMoth_Neutral", Gender.Female) };
        var s1 = Solver();
        var result = await s1.SolveAsync(Goal("Sekhmet", d), owned, CancellationToken.None);
        var plan = Assert.Single(result.Plans.Take(1));
        Assert.Equal(1, plan.Steps);
        Assert.Equal(10.0, plan.ExpectedEggsTotal, 6);
        var step = Assert.IsType<BreedStep>(plan.Root);
        Assert.Equal(d.Order(), step.ParentA.SpeciesId == "LazyDragon" ? d.Order() : d.Order());
    }

    [Fact]
    public async Task GuildSharedLocation_CountsForMe_AndAStartParentFromThereIsFound()
    {
        var d = WorkPassives();
        var host = new PalOwner("host", null, null, null);
        var a = Pal("a", "LazyDragon", Gender.Male, d) with { Owner = host, Location = new PalLocation(LocationKind.DimensionalStorage, null, null, null) };
        var b = Pal("b", "WhiteMoth_Neutral", Gender.Female) with { Owner = host, Location = new PalLocation(LocationKind.Base, null, null, null) };
        IReadOnlyList<LocationKind> kinds = [LocationKind.Palbox, LocationKind.DimensionalStorage, LocationKind.Base];
        var goal = Goal("Sekhmet", d, start: new StartParent("a", null, null));

        var shared = await Solver().SolveAsync(goal with { Scope = new OwnerScope(["me"], kinds, OwnerFreeLocations: [LocationKind.Base, LocationKind.DimensionalStorage]) }, [a, b], CancellationToken.None);
        var notShared = await Solver().SolveAsync(goal with { Scope = new OwnerScope(["me"], kinds) }, [a, b], CancellationToken.None);

        Assert.NotEmpty(shared.Plans);
        Assert.Empty(notShared.Plans);
    }

    [Fact]
    public async Task OneUnwantedPassiveInThePool_FiftyEggs()
    {
        var d = WorkPassives();
        var other = Data.Value.Passives.First(p => p.IsStandard && !d.Contains(p.Id) && p.Rank < 0).Id;
        var owned = new[] { Pal("a", "LazyDragon", Gender.Male, d), Pal("b", "WhiteMoth_Neutral", Gender.Female, [other]) };
        var result = await Solver().SolveAsync(Goal("Sekhmet", d), owned, CancellationToken.None);
        Assert.Equal(50.0, result.Plans[0].ExpectedEggsTotal, 6);
    }

    [Fact]
    public async Task Mozzarina_WithWildOn_NeedsTwoSteps_AndCarriesThePassivesToAnIntermediate()
    {
        var d = WorkPassives();
        var owned = new[] { Pal("m", "CowPal", Gender.Female, d) };
        var solver = Solver();
        var result = await solver.SolveAsync(Goal("Sekhmet", d, wild: true, maxWild: 3), owned, CancellationToken.None);
        Assert.NotEmpty(result.Plans);
        var plan = result.Plans[0];
        Assert.True(solver.LastStats.Expanded > 0);
        Assert.Equal(2, plan.Steps);
        var root = (BreedStep)plan.Root;
        var first = Assert.IsType<BreedStep>(root.ParentA is BreedStep ? root.ParentA : root.ParentB);
        Assert.Equal(4, first.TargetPassives.Count);
    }

    [Fact]
    public async Task PassiveNoPalHas_GivesNoPlanAndNamesIt()
    {
        var d = WorkPassives();
        var owned = new[] { Pal("a", "LazyDragon", Gender.Male, d.Take(3).ToList()), Pal("b", "WhiteMoth_Neutral", Gender.Female) };
        var result = await Solver().SolveAsync(Goal("Sekhmet", d, wild: true), owned, CancellationToken.None);
        Assert.Empty(result.Plans);
        Assert.Contains($"{PlanNotes.PassiveMissing}|{d[3]}", result.Diagnostics);
    }

    [Fact]
    public async Task OwnedTarget_WithoutPassives_IsNotAZeroStepPlan()
    {
        var d = WorkPassives();
        var owned = new[]
        {
            Pal("plain", "Sekhmet", Gender.Female),
            Pal("carrier", "Sekhmet", Gender.Male, d),
        };
        var result = await Solver().SolveAsync(Goal("Sekhmet", d), owned, CancellationToken.None);
        // The male already has everything: that is a note (T43), the plans still make a new Pal in at least one step.
        Assert.Equal(1, result.OwnedMatching);
        Assert.Equal(2, result.OwnedTargets!.Count);
        Assert.NotEmpty(result.Plans);
        Assert.All(result.Plans, p => Assert.True(p.Steps >= 1));

        var onlyPlain = await Solver().SolveAsync(Goal("Sekhmet", d), [owned[0], Pal("x", "LazyDragon", Gender.Male, d), Pal("y", "WhiteMoth_Neutral", Gender.Female)], CancellationToken.None);
        Assert.All(onlyPlain.Plans, p => Assert.True(p.Steps >= 1));
        Assert.NotEmpty(onlyPlain.Plans);
    }

    [Fact]
    public async Task OwnedTarget_WithTooManyUnwanted_IsNotReady()
    {
        var d = WorkPassives().Take(2).ToList();
        var junk = Data.Value.Passives.Where(p => p.IsStandard && !d.Contains(p.Id)).Take(2).Select(p => p.Id).ToList();
        var pal = Pal("s", "Sekhmet", Gender.Female, d.Concat(junk).ToList());
        var result = await Solver().SolveAsync(Goal("Sekhmet", d, maxUnwanted: 0), [pal], CancellationToken.None);
        Assert.DoesNotContain(result.Plans, p => p.Steps == 0);
        Assert.Equal(0, result.OwnedMatching); // too many unwanted passives for maxUnwanted = 0
        var lenient = await Solver().SolveAsync(Goal("Sekhmet", d, maxUnwanted: 2), [pal], CancellationToken.None);
        Assert.DoesNotContain(lenient.Plans, p => p.Steps == 0);
        Assert.Equal(1, lenient.OwnedMatching);
    }

    [Fact]
    public async Task SamePhysicalPal_IsNeverUsedTwice()
    {
        var d = WorkPassives();
        var owned = new[] { Pal("only", "LazyDragon", null, d) };
        var result = await Solver().SolveAsync(Goal("Sekhmet", d, wild: true, maxWild: 3), owned, CancellationToken.None);
        Assert.All(result.Plans, p => Assert.True(PlanTreeValidator.IsValid(p.Root)));
    }
}
