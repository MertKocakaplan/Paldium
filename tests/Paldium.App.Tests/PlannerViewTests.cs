using Paldium.App.Pages.PlannerParts;
using Paldium.App.Services;
using Paldium.Core.Planning;

namespace Paldium.App.Tests;

public class PlannerViewTests
{
    private static readonly PlanResult Empty = new([], [], [], false);

    [Fact]
    public void WithAPlan_TheTreeShows_NotTheDiagnosis()
    {
        Assert.Equal(PlanText.CanvasView.Tree, PlanText.ChooseCanvas(computing: false, hasPlan: true, editingInputs: false, emptyResult: false, hasTarget: true));
        // A plan beside a stale "empty" flag must still draw the tree.
        Assert.Equal(PlanText.CanvasView.Tree, PlanText.ChooseCanvas(false, true, false, true, true));
        Assert.Equal(PlanText.CanvasView.Tree, PlanText.ChooseCanvas(true, true, false, false, true));
    }

    [Fact]
    public void WithoutAResult_NoDiagnosisShows()
    {
        Assert.Equal(PlanText.CanvasView.Input, PlanText.ChooseCanvas(computing: false, hasPlan: false, editingInputs: false, emptyResult: false, hasTarget: true));
        Assert.Equal(PlanText.CanvasView.Skeleton, PlanText.ChooseCanvas(true, false, false, false, true));
        Assert.Equal(PlanText.CanvasView.Diagnosis, PlanText.ChooseCanvas(false, false, false, true, true));
    }

    [Fact]
    public void EditingTheTarget_ShowsTheInputView()
    {
        Assert.Equal(PlanText.CanvasView.Input, PlanText.ChooseCanvas(false, true, true, false, true));
    }

    [Fact]
    public void ResetAndNoResult_AreNotAnEmptyResult()
    {
        using var dir = new TempDir();
        var repo = new Paldium.Core.Owned.OwnedPalRepository(PlannerStateServiceTests.GameDataForTests);
        var settings = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        using var scope = new OwnedScopeService(repo, settings);
        using var state = new PlannerStateService(repo, scope);
        Assert.False(state.HasEmptyResult);
        state.SetResult(Empty, 1, "Anubis", resetSelection: true);
        Assert.True(state.HasEmptyResult);
        state.Reset();
        Assert.False(state.HasEmptyResult);
    }

    [Fact]
    public void ThreePlus_StoresNoPracticalLimit()
    {
        var planner = SettingsPlannerOptions.WithWild(new PlannerSettings(), 3);
        Assert.True(planner.AllowWild);
        Assert.Equal(10, planner.MaxWild);
        Assert.Equal(3, SettingsPlannerOptions.WildChoice(planner));
        Assert.Equal(2, SettingsPlannerOptions.WithWild(planner, 2).MaxWild);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pb-view-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
