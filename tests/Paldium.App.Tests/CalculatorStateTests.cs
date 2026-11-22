using Paldium.App.Pages.CalculatorTabs;
using Paldium.App.Services;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Probability;

namespace Paldium.App.Tests;

public class CalculatorStateTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(FindDataFolder()));

    private static readonly Lazy<BreedingCalculator> Breeding =
        new(() => BreedingCalculator.Load(Data.Value, FindDataFolder()));

    private static string FindDataFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data");
    }

    private static OwnedPal MakePal(string uid, string species, Gender gender, params string[] passives) =>
        new(uid, null, species, gender, 10, 0, passives, null, [], [], null, null,
            new PalLocation(LocationKind.Palbox, null, 0, null, 1),
            PalSourceKind.Save, "save", DateTimeOffset.UnixEpoch, false, false);

    private static OwnedPalRepository RepoWith(params OwnedPal[] pals)
    {
        var repo = new OwnedPalRepository(Data.Value);
        repo.PublishSourceSnapshot("save", pals, new SourceStatus("save", SourceState.Ok, null, pals.Length, null));
        return repo;
    }

    [Fact]
    public void NormalizeMode_HandlesTurkishAndEnglishAliases()
    {
        Assert.Equal(CalculatorStateService.ModePair, CalculatorStateService.NormalizeMode("cift"));
        Assert.Equal(CalculatorStateService.ModePair, CalculatorStateService.NormalizeMode("pair"));
        Assert.Equal(CalculatorStateService.ModeParents, CalculatorStateService.NormalizeMode("ebeveynler"));
        Assert.Equal(CalculatorStateService.ModeParents, CalculatorStateService.NormalizeMode("parents"));
        Assert.Equal(CalculatorStateService.ModeParents, CalculatorStateService.NormalizeMode("target"));
        Assert.Equal(CalculatorStateService.ModeOdds, CalculatorStateService.NormalizeMode("olasilik"));
        Assert.Equal(CalculatorStateService.ModeOdds, CalculatorStateService.NormalizeMode("odds"));
        Assert.Equal(CalculatorStateService.ModeMutation, CalculatorStateService.NormalizeMode("mutasyon"));
        Assert.Equal(CalculatorStateService.ModeMutation, CalculatorStateService.NormalizeMode("mutation"));
        Assert.Equal(CalculatorStateService.ModePair, CalculatorStateService.NormalizeMode("unknown"));
    }

    [Fact]
    public void ApplyQuery_PrefersOwnedKeyOverSpeciesId_AndHandlesTarget()
    {
        var palA = MakePal("uid-a", "Sekhmet", Gender.Male);
        var palB = MakePal("uid-b", "LazyDragon", Gender.Female);
        var repo = RepoWith(palA, palB);
        var state = new CalculatorStateService();

        var applied = state.ApplyQuery(null, palA.SelectionKey, "WhiteMoth_Neutral", null, Data.Value, repo);
        Assert.True(applied);
        Assert.Equal(CalculatorStateService.ModePair, state.Mode);
        Assert.True(state.OwnedModeA);
        Assert.Equal(palA.SelectionKey, state.OwnedAKey);
        Assert.Equal("Sekhmet", state.SpeciesAId);
        Assert.False(state.OwnedModeB);
        Assert.Null(state.OwnedBKey);
        Assert.Equal("WhiteMoth_Neutral", state.SpeciesBId);

        // Identical navigation signature is ignored on re-render.
        Assert.False(state.ApplyQuery(null, palA.SelectionKey, "WhiteMoth_Neutral", null, Data.Value, repo));

        // Target-only query switches to Hedef -> ebeveynler.
        Assert.True(state.ApplyQuery(null, null, null, "Sekhmet", Data.Value, repo));
        Assert.Equal(CalculatorStateService.ModeParents, state.Mode);
        Assert.Equal("Sekhmet", state.TargetSpeciesId);
    }

    [Fact]
    public void ResolvePair_ComputesValidSpeciesAndOwnedChildren_AndDetectsSameGender()
    {
        var maleDragon = MakePal("m-dragon", "LazyDragon", Gender.Male, "Noukin");
        var maleMoth = MakePal("m-moth", "WhiteMoth_Neutral", Gender.Male, "CraftSpeed_up2");
        var femaleMoth = MakePal("f-moth", "WhiteMoth_Neutral", Gender.Female, "CraftSpeed_up2");
        var repo = RepoWith(maleDragon, maleMoth, femaleMoth);

        var state = new CalculatorStateService
        {
            OwnedModeA = true,
            OwnedAKey = maleDragon.SelectionKey,
            SpeciesAId = "LazyDragon",
            OwnedModeB = true,
            OwnedBKey = femaleMoth.SelectionKey,
            SpeciesBId = "WhiteMoth_Neutral",
        };

        var valid = state.ResolvePair(Data.Value, Breeding.Value, repo);
        Assert.False(valid.InvalidPair);
        Assert.False(valid.SameGender);
        Assert.Equal("Sekhmet", valid.ValidOwnedChild?.Id);
        Assert.Single(valid.MatchingOutcomes);

        state.OwnedBKey = maleMoth.SelectionKey;
        var invalid = state.ResolvePair(Data.Value, Breeding.Value, repo);
        Assert.True(invalid.InvalidPair);
        Assert.True(invalid.SameGender);
        Assert.Equal(Gender.Male, invalid.ConflictingGender);
        Assert.Equal("Sekhmet", invalid.FallbackChild?.Id);
        Assert.Null(invalid.ValidOwnedChild);
    }

    [Fact]
    public void ResolvePair_DetectsRecipeGenderMismatch_EvenWhenOppositeGenders()
    {
        var female = MakePal("f-a", "SheepBall", Gender.Female);
        var male = MakePal("m-b", "PinkCat", Gender.Male);
        var repo = RepoWith(female, male);

        var state = new CalculatorStateService
        {
            OwnedModeA = true,
            OwnedAKey = female.SelectionKey,
            OwnedModeB = true,
            OwnedBKey = male.SelectionKey,
        };

        var res = state.ResolvePair(Data.Value, new StubMaleFirstBreeding(), repo);
        Assert.True(res.InvalidPair);
        Assert.False(res.SameGender);
        Assert.Null(res.ConflictingGender);
        Assert.Empty(res.MatchingOutcomes);
        Assert.Equal("Sekhmet", res.FallbackChild?.Id);
        Assert.Null(res.ValidOwnedChild);
    }

    [Fact]
    public void SwapParents_SwitchToSpeciesMode_AndSyncDesiredPassives_WorkAsExpected()
    {
        var positiveId = Data.Value.Passives.First(p => p.Rank > 0).Id;
        var negativeId = Data.Value.Passives.First(p => p.Rank < 0).Id;
        var palA = MakePal("a", "LazyDragon", Gender.Male, positiveId, negativeId);
        var palB = MakePal("b", "WhiteMoth_Neutral", Gender.Female, positiveId);

        var state = new CalculatorStateService
        {
            OwnedModeA = true,
            OwnedAKey = palA.SelectionKey,
            SpeciesAId = "LazyDragon",
            OwnedModeB = false,
            SpeciesBId = "WhiteMoth_Neutral",
        };

        state.SwapParents();
        Assert.False(state.OwnedModeA);
        Assert.Equal("WhiteMoth_Neutral", state.SpeciesAId);
        Assert.True(state.OwnedModeB);
        Assert.Equal(palA.SelectionKey, state.OwnedBKey);

        state.SyncPairDesiredFromParents(palA, palB, Data.Value);
        Assert.Equal([positiveId], state.PairDesiredPassives);

        state.SwitchPairToSpeciesMode(palA, palB);
        Assert.False(state.OwnedModeA);
        Assert.False(state.OwnedModeB);
        Assert.Null(state.OwnedAKey);
        Assert.Null(state.OwnedBKey);
        Assert.Equal("LazyDragon", state.SpeciesAId);
        Assert.Equal("WhiteMoth_Neutral", state.SpeciesBId);
    }

    [Fact]
    public void PassiveOddsFormula_MatchesCalculator_ForMockupExampleAndSpecialCake()
    {
        var calc = new PassiveOddsCalculator(Data.Value);
        string[] parentA = ["p1", "p2", "u1"];
        string[] parentB = ["p3", "u2"];
        string[] desired = ["p1", "p2", "p3"];

        var expected = calc.Compute(new PassiveOddsQuery(parentA, parentB, desired, 0, null));
        var breakdown = PassiveOddsFormula.Evaluate(Data.Value, parentA, parentB, desired, 0, null);

        Assert.Equal(expected.ProbabilityPerEgg, breakdown.TotalProbability, 9);
        Assert.Equal(5, breakdown.PoolSize);
        Assert.False(breakdown.HasMultipleTerms);
        Assert.NotNull(breakdown.DominantTerm);
        Assert.Equal(3, breakdown.DominantTerm!.InheritedCount);
        Assert.Equal("1/10", breakdown.DominantTerm.WaysFraction);
        Assert.Equal(0.20, breakdown.DominantTerm.InheritProbability, 6);
        Assert.Equal(0.40, breakdown.DominantTerm.RandomProbability, 6);
        Assert.Equal(0.008, breakdown.DominantTerm.TermProbability, 6);

        var cakeExpected = calc.Compute(new PassiveOddsQuery(parentA, parentB, desired, 0, "Cake05"));
        var cakeBreakdown = PassiveOddsFormula.Evaluate(Data.Value, parentA, parentB, desired, 0, "Cake05");
        Assert.Equal(cakeExpected.ProbabilityPerEgg, cakeBreakdown.TotalProbability, 9);
        Assert.Equal(0, cakeBreakdown.TotalProbability);
        Assert.Null(cakeBreakdown.DominantTerm);
    }

    [Fact]
    public void PassiveOddsFormula_MatchesCalculator_AcrossSmallPoolsAndUnknowns()
    {
        var calc = new PassiveOddsCalculator(Data.Value);
        string[] parentA = ["p1", "p2"];
        string[] parentB = ["u1"];
        string[] desired = ["p1", "p2"];

        var expected = calc.Compute(new PassiveOddsQuery(parentA, parentB, desired, 1, null, 0, 1));
        var breakdown = PassiveOddsFormula.Evaluate(Data.Value, parentA, parentB, desired, 1, null, 0, 1);

        Assert.Equal(expected.ProbabilityPerEgg, breakdown.TotalProbability, 9);
        Assert.Equal(4, breakdown.PoolSize);
        Assert.True(breakdown.HasMultipleTerms);
        Assert.NotNull(breakdown.DominantTerm);

        // Smaller pool than MaxPassives where multiple rolls cap to pool size
        var smallExpected = calc.Compute(new PassiveOddsQuery(["p1"], ["p2"], desired, 0, null));
        var smallBreakdown = PassiveOddsFormula.Evaluate(Data.Value, ["p1"], ["p2"], desired, 0, null);
        Assert.Equal(smallExpected.ProbabilityPerEgg, smallBreakdown.TotalProbability, 9);
        Assert.Equal(0.60, smallBreakdown.DominantTerm!.InheritProbability, 6);
        Assert.True(smallBreakdown.DominantTerm.IsPoolCap);
    }

    [Fact]
    public void PassiveOddsFormula_DetectsMissingDesiredPassive()
    {
        var breakdown = PassiveOddsFormula.Evaluate(Data.Value, ["p1"], ["p2"], ["p1", "p3"], 0, null);
        Assert.Equal(["p3"], breakdown.MissingDesiredIds);
        Assert.Equal(0, breakdown.TotalProbability);
        Assert.Null(breakdown.DominantTerm);
    }

    [Fact]
    public void ApplyQuery_WithPassive_OpensPassiveOddsAndSelectsDesired()
    {
        var repo = RepoWith();
        var state = new CalculatorStateService();
        var passive = Data.Value.Passives.First();

        var applied = state.ApplyQuery("olasilik", null, null, null, Data.Value, repo, passive.Id);

        Assert.True(applied);
        Assert.Equal(CalculatorStateService.ModeOdds, state.Mode);
        Assert.Contains(passive.Id, state.OddsDesired, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyQuery_WithForce_ReappliesAfterModeSwitch_AndEmptyQueryClearsCache()
    {
        var repo = RepoWith();
        var state = new CalculatorStateService();
        var passive = Data.Value.Passives.First();

        Assert.True(state.ApplyQuery("olasilik", null, null, null, Data.Value, repo, passive.Id));
        state.Mode = CalculatorStateService.ModePair;
        state.OddsDesired.Clear();

        Assert.False(state.ApplyQuery("olasilik", null, null, null, Data.Value, repo, passive.Id));
        Assert.True(state.ApplyQuery("olasilik", null, null, null, Data.Value, repo, passive.Id, force: true));
        Assert.Equal(CalculatorStateService.ModeOdds, state.Mode);
        Assert.Contains(passive.Id, state.OddsDesired, StringComparer.OrdinalIgnoreCase);

        Assert.False(state.ApplyQuery(null, null, null, null, Data.Value, repo));
        Assert.True(state.ApplyQuery("olasilik", null, null, null, Data.Value, repo, passive.Id));
    }

    private sealed class StubMaleFirstBreeding : IBreedingCalculator
    {
        public string GetChild(string a, string b, Gender? ga = null, Gender? gb = null) => "Sekhmet";

        public IReadOnlyList<ChildOutcome> GetChildOutcomes(string a, string b) =>
            [new ChildOutcome("Sekhmet", GenderReq.Male, GenderReq.Female)];

        public IReadOnlyList<ParentPair> GetParents(string childId) => [];
        public double ChildGenderProbability(string childId, Gender g) => 0.5;
        public int MinStepsToTarget(string fromSpecies, string targetSpecies) => 1;
    }
}
