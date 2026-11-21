using Paldium.Core.Owned;
using Paldium.Core.Planning;

namespace Paldium.App.Services;

/// <summary>A plan taken from the "which preferred Pal is best?" table that is not one of the alternatives; <see cref="Label"/> names the Pal.</summary>
public sealed record ExtraPlan(string Label, BreedingPlan Plan);

/// <summary>
/// What the Planner page shows (T63): the result, the selected plan and the steps ticked off. It lives as long as the app, so leaving the page and
/// coming back shows the same plan. A change of the Pals or of the scope after the calculation does not delete the plan: <see cref="IsStale"/> is
/// set and the page says "your Pals changed, recalculate". A new calculation starts clean.
/// </summary>
public sealed class PlannerStateService : IDisposable
{
    private readonly IOwnedPalRepository _repository;
    private readonly OwnedScopeService _scope;
    private readonly List<ExtraPlan> _extra = [];
    private readonly Dictionary<string, HashSet<int>> _doneBySignature = new(StringComparer.Ordinal);
    private HashSet<int> _emptyDone = [];
    private readonly Dictionary<string, Dictionary<int, Paldium.Core.Domain.Gender>> _flexBySignature = new(StringComparer.Ordinal);

    public PlannerStateService(IOwnedPalRepository repository, OwnedScopeService scope)
    {
        _repository = repository;
        _scope = scope;
        _repository.Changed += OnRepositoryChanged;
        _scope.Changed += OnScopeChanged;
    }

    /// <summary>Raised when the state changed from outside the page (the Pals or the scope changed).</summary>
    public event EventHandler? Changed;

    public PlanResult? Result { get; private set; }

    /// <summary>True once a calculation has been started for the current flow; enables automatic recompute after edits.</summary>
    public bool HasSolved { get; set; }

    /// <summary>A calculation finished and found no plan. False after a reset or while nothing was calculated (no "no plan" screen for those).</summary>
    public bool HasEmptyResult => Result != null && Plans.Count == 0;

    /// <summary>The target species the result was made for.</summary>
    public string? ResultTarget { get; private set; }

    /// <summary>Version of the Pal snapshot the result was made with.</summary>
    public long ComputedVersion { get; private set; }

    public int Selected { get; private set; }

    /// <summary>Indexes of the steps ticked off for the currently selected plan (stored per plan signature).</summary>
    public HashSet<int> Done
    {
        get
        {
            var plan = CurrentPlan;
            if (plan == null) return _emptyDone;
            var sig = Signature(plan);
            if (!_doneBySignature.TryGetValue(sig, out var set))
            {
                set = [];
                _doneBySignature[sig] = set;
            }
            return set;
        }
    }

    /// <summary>The gender that hatched from a flexible step (T105), by step index, for the selected plan; it decides the next step's partner.</summary>
    public Dictionary<int, Paldium.Core.Domain.Gender> FlexChoices
    {
        get
        {
            var plan = CurrentPlan;
            if (plan == null) return [];
            var sig = Signature(plan);
            if (!_flexBySignature.TryGetValue(sig, out var map))
            {
                map = [];
                _flexBySignature[sig] = map;
            }

            return map;
        }
    }

    /// <summary>True when the Pals or the scope changed after the calculation.</summary>
    public bool IsStale { get; private set; }

    public IReadOnlyList<ExtraPlan> Extra => _extra;

    /// <summary>The alternatives of the result followed by the plans added from the preferred table.</summary>
    public IReadOnlyList<BreedingPlan> Plans => Result == null ? [] : [.. Result.Plans, .. _extra.Select(e => e.Plan)];

    /// <summary>Currently selected plan, or null when no plan exists.</summary>
    public BreedingPlan? CurrentPlan => Plans.Count > 0 && Selected >= 0 && Selected < Plans.Count ? Plans[Selected] : null;

    /// <summary>One entry per plan of <see cref="Plans"/>: the label of an extra plan, null for an alternative.</summary>
    public IReadOnlyList<string?> Labels => Result == null ? [] : [.. Result.Plans.Select(_ => (string?)null), .. _extra.Select(e => (string?)e.Label)];

    /// <summary>Stores a result. <paramref name="resetSelection"/> starts at the first plan with nothing ticked (a new calculation, not a recompute).</summary>
    public void SetResult(PlanResult result, long version, string target, bool resetSelection)
    {
        var prevSignature = !resetSelection && CurrentPlan is { } current ? Signature(current) : null;
        Result = result;
        ComputedVersion = version;
        ResultTarget = target;
        HasSolved = true;
        IsStale = false;
        if (resetSelection)
        {
            Selected = 0;
            _doneBySignature.Clear();
            _flexBySignature.Clear();
            _emptyDone = [];
            _extra.Clear();
            return;
        }

        _extra.Clear();
        if (prevSignature != null)
        {
            if (Selected >= 0 && Selected < result.Plans.Count && Signature(result.Plans[Selected]) == prevSignature)
                return;

            for (var i = 0; i < result.Plans.Count; i++)
            {
                if (Signature(result.Plans[i]) == prevSignature)
                {
                    Selected = i;
                    return;
                }
            }
            Selected = 0;
            return;
        }

        if (Selected >= Plans.Count)
            Selected = 0;
    }

    /// <summary>Updates result metadata (diagnostics/feasibility) without replacing the currently visible plans.</summary>
    public void RefreshMetadata(PlanResult updated)
    {
        if (Result == null) return;
        Result = Result with
        {
            Feasibility = updated.Feasibility,
            Diagnostics = updated.Diagnostics,
            TimedOut = updated.TimedOut,
            OwnedTargets = updated.OwnedTargets,
            OwnedMatching = updated.OwnedMatching,
        };
    }

    public void Select(int index)
    {
        Selected = Math.Clamp(index, 0, Math.Max(0, Plans.Count - 1));
    }

    /// <summary>Toggles completion of one step on the currently selected plan.</summary>
    public void ToggleStep(int stepIndex)
    {
        var done = Done;
        if (!done.Add(stepIndex)) done.Remove(stepIndex);
    }

    /// <summary>
    /// Makes the best plan of a preferred Pal the selected plan: the matching alternative when there is one, else the plan is added with a label.
    /// Returns the index of the selected plan.
    /// </summary>
    public int SelectPlan(BreedingPlan plan, string label)
    {
        var signature = Signature(plan);
        var all = Plans;
        var index = -1;
        for (var i = 0; i < all.Count; i++)
            if (Signature(all[i]) == signature) { index = i; break; }
        if (index < 0)
        {
            _extra.Add(new ExtraPlan(label, plan));
            index = Plans.Count - 1;
        }

        Select(index);
        return index;
    }

    /// <summary>Forgets everything (another target was asked for).</summary>
    public void Reset()
    {
        Result = null;
        ResultTarget = null;
        Selected = 0;
        _doneBySignature.Clear();
        _flexBySignature.Clear();
        _emptyDone = [];
        IsStale = false;
        _extra.Clear();
    }

    /// <summary>The same plan: the same steps with the same Pals (the plans of the preferred table are made by another search).</summary>
    public static string Signature(BreedingPlan plan) => Signature(plan.Root);

    private static string Signature(PlanNode node) => node switch
    {
        OwnedLeaf owned => "o:" + owned.Pal.Uid,
        WildLeaf wild => $"w:{wild.SpeciesId}:{wild.Gender}",
        BreedStep step => $"({Signature(step.ParentA)}+{Signature(step.ParentB)}>{step.ChildSpeciesId}:{step.Gender})",
        _ => node.SpeciesId,
    };

    private void OnRepositoryChanged(object? sender, OwnedPalSnapshot snapshot)
    {
        if (Result == null || IsStale || snapshot.Version == ComputedVersion) return;
        IsStale = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnScopeChanged(object? sender, EventArgs e)
    {
        if (Result == null || IsStale) return;
        IsStale = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _repository.Changed -= OnRepositoryChanged;
        _scope.Changed -= OnScopeChanged;
    }
}
