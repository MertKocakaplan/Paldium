using Paldium.App.Pages.PlannerParts;
using Paldium.Core.Domain;
using Paldium.Core.Planning;
using Paldium.Core.Probability;

namespace Paldium.App.Tests;

public class NodeAnalysisTests
{
    private static readonly PassiveOddsCalculator Odds = new(PlannerStateServiceTests.GameDataForTests);

    private static OwnedPal Pal(string uid, string species, Gender gender, params string[] passives) =>
        new(uid, null, species, gender, 10, 1, passives, null, [], [], null, null,
            new PalLocation(LocationKind.Palbox, null, 14, null, 1), PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    private static BreedStep Step(PlanNode a, PlanNode b, string child, GenderReq gender, int maxUnwanted, double success, params string[] passives) =>
        new(a, b, child, gender, passives, maxUnwanted, null, success, 1 / success);

    [Fact]
    public void EveryEgg_IsConsistentWithThePlansOwnSuccessChance()
    {
        // Two owned parents that together hold four wanted passives; the step wants three of them, a male.
        string[] d = ["Artisan", "Serious", "Nocturnal", "Lucky"];
        var a = new OwnedLeaf(Pal("a", "CowPal", Gender.Female, d[0], d[1]));
        var b = new OwnedLeaf(Pal("b", "SheepBall", Gender.Male, d[2], d[3]));
        var step = Step(a, b, "Anubis", GenderReq.Male, 0, 0.06, d[0], d[1], d[2]);

        var dist = NodeAnalysis.StepOutcomes(Odds, step, d, genderRight: 0.5, flexible: false);
        Assert.Equal(1.0, dist.Total, 9);
        Assert.Equal(Odds.Probability(4, 0, 3, 0) * 0.5, dist.PlanSuccess, 9);
        Assert.Equal([1, 5, 5, 1, 3, 85], new[] { dist.Wanted, dist.ExtraWanted, dist.OtherGenderExtra, dist.OtherGender, dist.ExtraUnwanted, dist.Missing }.Select(x => (int)Math.Round(x * 100)));

        // A flexible step has no "other gender" classes and the gender does not halve the success.
        var flex = NodeAnalysis.StepOutcomes(Odds, step, d, 0.5, flexible: true);
        Assert.Equal(0, flex.OtherGender + flex.OtherGenderExtra, 12);
        Assert.Equal(Odds.Probability(4, 0, 3, 0), flex.PlanSuccess, 9);
    }

    [Fact]
    public void EveryCatch_SplitsTheWeightsAroundTheAssumption()
    {
        var rows = NodeAnalysis.WildCatch([10, 76, 14], assumed: 1, stepEggs: count => count == 0 ? 10.0 : count == 1 ? 50.0 : 83.0);
        Assert.Equal(["fewer", "equal", "more"], rows.Select(r => r.Kind));
        Assert.Equal([10, 76, 14], rows.Select(r => (int)Math.Round(r.Probability * 100)));
        Assert.Equal([10.0, 50.0, 83.0], rows.Select(r => r.StepEggs));

        // Assuming nothing: there is no "fewer" row.
        Assert.DoesNotContain(NodeAnalysis.WildCatch([10, 76, 14], 0, c => 1), r => r.Kind == "fewer");
    }

    [Fact]
    public void WithoutWantedPassives_NoPassiveClassesAreWorkedOut_AndAnImpossibleCatchStaysInfinite()
    {
        Assert.False(NodeAnalysis.PassivesMatter([]));
        Assert.True(NodeAnalysis.PassivesMatter(["Artisan"]));

        // With a wanted passive an unwanted catch can make the step impossible: the row says so (infinite eggs), it is never turned into a number.
        var rows = NodeAnalysis.WildCatch([10, 76, 14], assumed: 0, stepEggs: c => c == 0 ? 2.0 : double.PositiveInfinity);
        Assert.Equal(double.PositiveInfinity, rows.Single(r => r.Kind == "more").StepEggs);
        Assert.Equal(2.0, rows.Single(r => r.Kind == "equal").StepEggs);
    }

    [Fact]
    public void Verdict_SmallerLeftWins_TieGoesToThePal_NoPlanMeansRetry()
    {
        var a = new OwnedLeaf(Pal("a", "CowPal", Gender.Female, "Artisan"));
        var b = new OwnedLeaf(Pal("b", "SheepBall", Gender.Male));
        var step = Step(a, b, "Anubis", GenderReq.Female, 0, 0.1, "Artisan");
        string[] d = ["Artisan", "Serious"];

        Assert.True(NodeAnalysis.Decide(step, ["Artisan"], Gender.Female, d, withPal: 70.0, retry: 86.7).ContinueFromPal);
        Assert.True(NodeAnalysis.Decide(step, ["Artisan"], Gender.Female, d, withPal: 50.0, retry: 50.0).ContinueFromPal);
        Assert.False(NodeAnalysis.Decide(step, ["Artisan"], Gender.Female, d, withPal: 90.0, retry: 86.7).ContinueFromPal);
        Assert.False(NodeAnalysis.Decide(step, ["Artisan"], Gender.Female, d, withPal: null, retry: 86.7).ContinueFromPal);

        Assert.Equal(NodeAnalysis.VerdictKind.Missing, NodeAnalysis.Decide(step, [], Gender.Female, d, 10, 20).Kind);
        Assert.Equal(NodeAnalysis.VerdictKind.OtherGender, NodeAnalysis.Decide(step, ["Artisan"], Gender.Male, d, 10, 20).Kind);
        Assert.Equal(NodeAnalysis.VerdictKind.ExtraWanted, NodeAnalysis.Decide(step, ["Artisan", "Serious"], Gender.Female, d, 10, 20).Kind);
        Assert.Equal(NodeAnalysis.VerdictKind.Exact, NodeAnalysis.Decide(step, ["Artisan"], Gender.Female, d, 10, 20).Kind);
    }

    [Fact]
    public void FlexibleStep_ChoiceDecidesThePartner_AndTheLayoutMakesRoomForTheBackup()
    {
        var mother = new OwnedLeaf(Pal("m", "CowPal", Gender.Female, "Artisan"));
        var father = new OwnedLeaf(Pal("f", "SheepBall", Gender.Male));
        var hoocrates = new BreedStep(mother, father, "GrassGolem", GenderReq.Any, ["Artisan"], 0, null, 0.1, 10);
        var partner = new OwnedLeaf(Pal("p", "ChickenPal", Gender.Female, "Serious"));
        var backup = new OwnedLeaf(Pal("q", "ChickenPal", Gender.Male, "Serious"));
        var root = new BreedStep(hoocrates, partner, "Anubis", GenderReq.Any, ["Artisan", "Serious"], 0, null, 0.1, 10, backup);
        var plan = new BreedingPlan(root, 2, 20, 0, false, [], []);

        // A male hatched: it pairs with the female main partner. A female hatched: the backup (male) is used.
        var male = PlanText.ParentsInUse(plan, root, new Dictionary<int, Gender> { [0] = Gender.Male });
        Assert.Same(partner, male.B);
        var female = PlanText.ParentsInUse(plan, root, new Dictionary<int, Gender> { [0] = Gender.Female });
        Assert.Same(backup, female.B);
        Assert.Same(partner, PlanText.ParentsInUse(plan, root, new Dictionary<int, Gender>()).B);

        var layout = PlanTreeLayout.Build(plan, 0);
        var partnerNode = layout.Nodes.Single(n => ReferenceEquals(n.Node, partner));
        Assert.Same(backup, partnerNode.Flex!.Alt);
        Assert.Same(hoocrates, partnerNode.Flex.Flexible);
        Assert.Null(layout.Nodes.Single(n => ReferenceEquals(n.Node, mother)).Flex);
    }
}

public class NodeAnalysisT112Tests
{
    private static readonly PassiveOddsCalculator Odds = new(PlannerStateServiceTests.GameDataForTests);
    private static readonly Paldium.Core.Breeding.BreedingCalculator Breeding =
        Paldium.Core.Breeding.BreedingCalculator.Load(PlannerStateServiceTests.GameDataForTests, DataFolder());

    private static string DataFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data");
    }

    private static OwnedPal Pal(string uid, string species, Gender gender, params string[] passives) =>
        new(uid, null, species, gender, 10, 1, passives, null, [], [], null, null,
            new PalLocation(LocationKind.Palbox, null, 14, null, 1), PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    private static BreedStep Step(PlanNode a, PlanNode b, string child, GenderReq gender, int maxUnwanted, double success, string[] passives, PlanNode? alt = null) =>
        new(a, b, child, gender, passives, maxUnwanted, null, success, 1 / success, alt);

    [Fact]
    public void RemainingIfRetry_DoesNotCountTheStepsAlreadyDone()
    {
        var a = new OwnedLeaf(Pal("a", "CowPal", Gender.Female));
        var b = new OwnedLeaf(Pal("b", "SheepBall", Gender.Male));
        var first = Step(a, b, "WizardOwl", GenderReq.Female, 0, 0.5, []);
        var root = Step(first, new OwnedLeaf(Pal("c", "ChickenPal", Gender.Male)), "CuteFox", GenderReq.Any, 0, 0.25, []);
        var plan = new BreedingPlan(root, 2, 6, 0, false, [], []);
        var steps = PlanText.StepsOf(plan);
        Assert.Equal(6, NodeAnalysis.RemainingIfRetry(plan, steps[0]), 9);
        Assert.Equal(2, NodeAnalysis.RemainingIfRetry(plan, steps[0], new HashSet<int> { 1 }), 9);   // the second step is done
        Assert.Equal(4, NodeAnalysis.RemainingIfRetry(plan, steps[0], new HashSet<int> { 0 }), 9);   // a done step is not counted
    }

    [Fact]
    public void BackupPartner_MixesThePoolsByGender_AndTheHatchedGenderPicksOneBranch()
    {
        string[] d = ["Artisan", "Serious"];
        var holder = new OwnedLeaf(Pal("h", "CowPal", Gender.Female, d));
        var flexParent = Step(holder, new OwnedLeaf(Pal("w", "SheepBall", Gender.Male)), "WizardOwl", GenderReq.Any, 0, 0.1, d);
        var partner = new OwnedLeaf(Pal("p", "ChickenPal", Gender.Male, d[0]));
        var backup = new OwnedLeaf(Pal("q", "ChickenPal", Gender.Female));
        var root = Step(flexParent, partner, "CuteFox", GenderReq.Any, 0, 0.1, d, backup);
        var mixed = NodeAnalysis.StepOutcomesWithBackup(Odds, Breeding, root, d, 1.0, null);
        Assert.Equal(1.0, mixed.Total, 9);
        var mainOnly = NodeAnalysis.StepOutcomesWithBackup(Odds, Breeding, root, d, 1.0, Gender.Female);   // the Pal pairs with the male partner
        var backupOnly = NodeAnalysis.StepOutcomesWithBackup(Odds, Breeding, root, d, 1.0, Gender.Male);
        Assert.Equal(1.0, mainOnly.Total, 9);
        Assert.Equal(1.0, backupOnly.Total, 9);
        var wMain = Breeding.ChildGenderProbability("WizardOwl", Gender.Female);
        Assert.Equal(wMain * mainOnly.PlanSuccess + (1 - wMain) * backupOnly.PlanSuccess, mixed.PlanSuccess, 9);
    }

    [Fact]
    public void SamePath_WithAnotherPal_IsWorkedOutOnTheFixedTree()
    {
        string[] d = ["Artisan", "Serious"];
        var holder = Pal("h", "CowPal", Gender.Female, d);
        var worse = Pal("x", "CowPal", Gender.Female, d[0]);
        var partner = new OwnedLeaf(Pal("p", "SheepBall", Gender.Male));
        var step = Step(new OwnedLeaf(holder), partner, "WizardOwl", GenderReq.Female, 0, 1, d);
        var plan = new BreedingPlan(step, 1, 1, 0, false, [], []);
        var same = NodeAnalysis.EggsWithReplacement(plan, "h", holder, d, Odds, Breeding);
        var other = NodeAnalysis.EggsWithReplacement(plan, "h", worse, d, Odds, Breeding);
        var expected = 1.0 / (Odds.Probability(2, 0, 2, 0) * Breeding.ChildGenderProbability("WizardOwl", Gender.Female));
        Assert.Equal(expected, same, 9);
        Assert.True(other > same);      // one wanted passive fewer in the pool: the step wanting both is dearer (here impossible)
    }

    [Fact]
    public void CatchRowsFor_StepWithBackupPartner_StillAppliesChildGenderProbability()
    {
        string[] d = ["Artisan", "Serious"];
        var flexParent = Step(
            new OwnedLeaf(Pal("h", "CowPal", Gender.Female, d)),
            new OwnedLeaf(Pal("w", "SheepBall", Gender.Male)),
            "WizardOwl", GenderReq.Any, 0, 0.1, d);
        var wildPartner = new WildLeaf("ChickenPal", GenderReq.Female, 1, "Wild");
        var backup = new OwnedLeaf(Pal("q", "ChickenPal", Gender.Male));
        var root = Step(flexParent, wildPartner, "CuteFox", GenderReq.Female, 0, 0.05, d, backup);

        var rows = NodeAnalysis.CatchRowsFor(wildPartner, root, d, Odds, Breeding, PlannerStateServiceTests.GameDataForTests);
        var genderRight = Breeding.ChildGenderProbability("CuteFox", Gender.Female);
        var expectedEqual = 1.0 / (Odds.Probability(2, 1, 2, 0, 0) * genderRight);

        Assert.Equal(expectedEqual, rows.Single(r => r.Kind == "equal").StepEggs, 6);
    }

    [Fact]
    public void CatchRowsFor_GuaranteedUnwantedSpecies_HasNoRedundantFewerRow_AndUsesEffectiveAssumption()
    {
        string[] d = ["Artisan"];
        var lazy = new WildLeaf("LazyDragon", GenderReq.Male, 1, "Wild");
        var partner = new OwnedLeaf(Pal("p", "WhiteMoth_Neutral", Gender.Female, d));
        var step = Step(lazy, partner, "Sekhmet", GenderReq.Any, 0, 0.1, d);

        var rows = NodeAnalysis.CatchRowsFor(lazy, step, d, Odds, Breeding, PlannerStateServiceTests.GameDataForTests);
        Assert.DoesNotContain(rows, r => r.Kind == "fewer");
        var equal = rows.Single(r => r.Kind == "equal");
        var more = rows.Single(r => r.Kind == "more");
        Assert.Equal(0.86, equal.Probability, 6);
        Assert.Equal(1.0 / Odds.Probability(1, 1, 1, 0, 0), equal.StepEggs, 6);
        Assert.Equal(0.14, more.Probability, 6);
        Assert.Equal(1.0 / Odds.Probability(1, 2, 1, 0, 0), more.StepEggs, 6);
    }

    [Fact]
    public void PlanList_PathIds_ReturnsNonWildParentsForOneStepPlan()
    {
        var ownedA = new OwnedLeaf(Pal("a", "CowPal", Gender.Male));
        var ownedB = new OwnedLeaf(Pal("b", "SheepBall", Gender.Female));
        var wildB = new WildLeaf("SheepBall", GenderReq.Female, 1, "Wild");
        var wildA = new WildLeaf("CowPal", GenderReq.Male, 1, "Wild");

        var bothOwned = new BreedingPlan(Step(ownedA, ownedB, "Anubis", GenderReq.Any, 0, 1.0, []), 1, 1.0, 0, false, [], []);
        var oneWild = new BreedingPlan(Step(ownedA, wildB, "Anubis", GenderReq.Any, 0, 1.0, []), 1, 1.0, 1, false, [], []);
        var bothWild = new BreedingPlan(Step(wildA, wildB, "Anubis", GenderReq.Any, 0, 1.0, []), 1, 1.0, 2, false, [], []);

        Assert.Equal(["CowPal", "SheepBall"], PlanList.PathIds(bothOwned));
        Assert.Equal(["CowPal"], PlanList.PathIds(oneWild));
        Assert.Empty(PlanList.PathIds(bothWild));
    }
}
