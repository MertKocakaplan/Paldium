using Paldium.App.Pages.PlannerParts;
using Paldium.App.Services;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;

namespace Paldium.App.Tests;

public class PlannerStateServiceTests : IDisposable
{
    internal static Paldium.Core.Data.GameData GameDataForTests => Data.Value;

    private static readonly Lazy<GameData> Data = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return GameDataLoader.Load(Path.Combine(dir!.FullName, "data"));
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-planner-" + Guid.NewGuid().ToString("N"));
    private readonly OwnedPalRepository _repo = new(Data.Value);
    private readonly SettingsStore _settings;
    private readonly OwnedScopeService _scope;
    private readonly PlannerStateService _state;

    public PlannerStateServiceTests()
    {
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _scope = new OwnedScopeService(_repo, _settings);
        _state = new PlannerStateService(_repo, _scope);
    }

    public void Dispose()
    {
        _state.Dispose();
        _scope.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static OwnedPal Pal(string uid, string species, Gender gender) =>
        new(uid, null, species, gender, 10, 1, [], null, [], [], null, null, new PalLocation(LocationKind.Palbox, null, null, null),
            PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    private static BreedStep Step(PlanNode a, PlanNode b, string child = "Anubis") => new(a, b, child, GenderReq.Any, [], 0, null, 1, 1);

    private static BreedingPlan PlanOf(PlanNode root, int steps = 1) => new(root, steps, 1, 0, false, [], []);

    private static PlanResult ResultOf(params BreedingPlan[] plans) => new(plans, [], [], false);

    [Fact]
    public void TheResultSelectionAndTicks_AreKept_UntilAnotherCalculationStarts()
    {
        var plan = PlanOf(Step(new OwnedLeaf(Pal("a", "Lamball", Gender.Male)), new OwnedLeaf(Pal("b", "Lamball", Gender.Female))));
        _state.SetResult(ResultOf(plan, plan), 1, "Anubis", resetSelection: true);
        _state.Select(1);
        _state.Done.Add(0);

        // "Leaving the page": nothing in the service depends on the page.
        Assert.Same(plan, _state.Plans[1]);
        Assert.Equal(1, _state.Selected);
        Assert.Contains(0, _state.Done);

        _state.SetResult(ResultOf(plan, plan), 1, "Anubis", resetSelection: false); // an improvement keeps the selection and the ticks
        Assert.Equal(1, _state.Selected);
        Assert.Contains(0, _state.Done);

        _state.SetResult(ResultOf(plan), 2, "Anubis", resetSelection: true); // a new calculation starts clean
        Assert.Equal(0, _state.Selected);
        Assert.Empty(_state.Done);
    }

    [Fact]
    public void ChangedPals_MarkThePlanOutOfDate_WithoutDeletingIt()
    {
        var raised = 0;
        _state.Changed += (_, _) => raised++;
        var plan = PlanOf(new WildLeaf("Anubis", GenderReq.Any, 0, "Wild"), 0);
        _state.SetResult(ResultOf(plan), _repo.Current.Version, "Anubis", resetSelection: true);
        Assert.False(_state.IsStale);

        _repo.AddManual(new OwnedPalDraft { SpeciesId = "SheepBall" });

        Assert.True(_state.IsStale);
        Assert.NotNull(_state.Result);
        Assert.Equal(1, raised);

        _state.SetResult(ResultOf(plan), _repo.Current.Version, "Anubis", resetSelection: true); // a new calculation clears the flag
        Assert.False(_state.IsStale);
    }

    [Fact]
    public void NoResult_IsNeverStale_AndResetForgetsEverything()
    {
        _repo.AddManual(new OwnedPalDraft { SpeciesId = "SheepBall" });
        Assert.False(_state.IsStale);

        _state.SetResult(ResultOf(PlanOf(new WildLeaf("Anubis", GenderReq.Any, 0, "Wild"), 0)), 1, "Anubis", resetSelection: true);
        _state.Reset();
        Assert.Null(_state.Result);
        Assert.Empty(_state.Plans);
    }

    [Fact]
    public void ThePlanOfAPreferredPal_IsSelected_AndAddedWithALabelOnlyWhenItIsNotAnAlternative()
    {
        var a = Pal("a", "Lamball", Gender.Male);
        var b = Pal("b", "Lamball", Gender.Female);
        var c = Pal("c", "Lamball", Gender.Female);
        var alternative = PlanOf(Step(new OwnedLeaf(a), new OwnedLeaf(b)));
        _state.SetResult(ResultOf(alternative), 1, "Anubis", resetSelection: true);

        // The same plan made by another search is the alternative.
        var same = PlanOf(Step(new OwnedLeaf(a), new OwnedLeaf(b)));
        Assert.Equal(0, _state.SelectPlan(same, "Lamball"));
        Assert.Empty(_state.Extra);

        // A plan with another Pal is added with the label and selected.
        var other = PlanOf(Step(new OwnedLeaf(a), new OwnedLeaf(c)));
        Assert.Equal(1, _state.SelectPlan(other, "Lamball ♀"));
        Assert.Equal(2, _state.Plans.Count);
        Assert.Equal([null, "Lamball ♀"], _state.Labels);
        Assert.Equal(1, _state.Selected);

        // Choosing it again does not add it twice.
        Assert.Equal(1, _state.SelectPlan(PlanOf(Step(new OwnedLeaf(a), new OwnedLeaf(c))), "Lamball ♀"));
        Assert.Single(_state.Extra);
    }

    [Fact]
    public void TheChecklist_NamesTheStepNumberOfAParentFromAnEarlierStep()
    {
        var first = Step(new OwnedLeaf(Pal("a", "Lamball", Gender.Male)), new OwnedLeaf(Pal("b", "Lamball", Gender.Female)), "Cattiva");
        var second = Step(new OwnedLeaf(Pal("c", "Lamball", Gender.Male)), new WildLeaf("Pengullet", GenderReq.Any, 0, "Wild"), "Foxparks");
        var last = Step(first, second);
        var plan = PlanOf(last, 3);

        Assert.Equal(1, PlanText.StepNumber(plan, first));
        Assert.Equal(2, PlanText.StepNumber(plan, second));
        Assert.Equal(3, PlanText.StepNumber(plan, last));
        Assert.Equal(0, PlanText.StepNumber(plan, Step(first, second)));
    }

    [Fact]
    public void TheCardsListTheLeafParents_EachOnce_InTheOrderOfTheTree()
    {
        var a = new OwnedLeaf(Pal("a", "SheepBall", Gender.Male));
        var b = new WildLeaf("Pengullet", GenderReq.Female, 0, "Wild");
        var again = new OwnedLeaf(Pal("a2", "SheepBall", Gender.Male));
        var plan = PlanOf(Step(Step(a, b, "Cattiva"), again), 2);

        var leaves = PlanText.Leaves(plan);
        Assert.Equal([("SheepBall", (Gender?)Gender.Male), ("Pengullet", (Gender?)Gender.Female)], leaves);
    }

    [Fact]
    public void ScopeChangesFromTheQuickMenu_ReachTheServiceAndMarkThePlanOutOfDate()
    {
        var changed = 0;
        _scope.Changed += (_, _) => changed++;
        _state.SetResult(ResultOf(PlanOf(new WildLeaf("Anubis", GenderReq.Any, 0, "Wild"), 0)), _repo.Current.Version, "Anubis", resetSelection: true);

        _settings.Update(s => s with { Scope = s.Scope with { Mode = ScopeSettings.All } });

        Assert.True(changed >= 1);
        Assert.True(_state.IsStale);
    }

    [Fact]
    public void TheChecklist_ShowsTheIvsOfAnOwnedLeaf_WhenKnown()
    {
        var known = Pal("a", "SheepBall", Gender.Male) with { Ivs = new Ivs(85, 92, 70) };
        Assert.Equal("IV 85/92/70", PlanText.IvText(known));
        Assert.Null(PlanText.IvText(Pal("b", "SheepBall", Gender.Female)));
    }

    [Fact]
    public void DoneSteps_PersistByPlanSignature_WhenSwitchingPlanTabs()
    {
        var planA = PlanOf(Step(new OwnedLeaf(Pal("a", "Lamball", Gender.Male)), new OwnedLeaf(Pal("b", "Lamball", Gender.Female))));
        var planB = PlanOf(Step(new OwnedLeaf(Pal("c", "Lamball", Gender.Male)), new OwnedLeaf(Pal("d", "Lamball", Gender.Female))));
        _state.SetResult(ResultOf(planA, planB), 1, "Anubis", resetSelection: true);

        _state.Done.Add(0);
        Assert.Contains(0, _state.Done);

        _state.Select(1);
        Assert.Empty(_state.Done);
        _state.ToggleStep(0);
        Assert.Contains(0, _state.Done);

        _state.Select(0);
        Assert.Contains(0, _state.Done);
    }

    [Fact]
    public void SetResult_WithoutReset_SelectsPlanWithMatchingSignature_EvenWhenIndexChanges()
    {
        var planA = PlanOf(Step(new OwnedLeaf(Pal("a", "Lamball", Gender.Male)), new OwnedLeaf(Pal("b", "Lamball", Gender.Female))));
        var planB = PlanOf(Step(new OwnedLeaf(Pal("c", "Lamball", Gender.Male)), new OwnedLeaf(Pal("d", "Lamball", Gender.Female))));
        var planC = PlanOf(Step(new OwnedLeaf(Pal("e", "Lamball", Gender.Male)), new OwnedLeaf(Pal("f", "Lamball", Gender.Female))));

        _state.SetResult(ResultOf(planA, planB), 1, "Anubis", resetSelection: true);
        _state.Select(1);
        _state.ToggleStep(0);

        _state.SetResult(ResultOf(planA, planC, planB), 2, "Anubis", resetSelection: false);
        Assert.Equal(2, _state.Selected);
        Assert.Contains(0, _state.Done);
        Assert.False(_state.IsStale);
    }
}
