using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.Core.Planning;

// Plan model (ARCHITECTURE 4.6) and the query / result records of the planners (ARCHITECTURE 5).

public abstract record PlanNode(string SpeciesId, GenderReq Gender);

/// <summary>A Pal the user owns, used as it is.</summary>
public sealed record OwnedLeaf(OwnedPal Pal) : PlanNode(Pal.SpeciesId, ToReq(Pal.Gender))
{
    internal static GenderReq ToReq(Gender? gender) => gender switch
    {
        Domain.Gender.Male => GenderReq.Male,
        Domain.Gender.Female => GenderReq.Female,
        _ => GenderReq.Any,
    };
}

/// <summary>A Pal the user has to obtain without breeding; <see cref="ObtainMethod"/> is the easiest way (ARCHITECTURE 7.3 order).</summary>
public sealed record WildLeaf(string SpeciesId, GenderReq Gender, int AssumedUnwantedPassives, string ObtainMethod)
    : PlanNode(SpeciesId, Gender);

public sealed record BreedStep(
    PlanNode ParentA, PlanNode ParentB, string ChildSpeciesId, GenderReq ChildGender,
    IReadOnlyList<string> TargetPassives, int MaxUnwanted, string? CakeId,
    double SuccessProbabilityPerEgg, double ExpectedEggs, PlanNode? AltPartner = null) : PlanNode(ChildSpeciesId, ChildGender);

/// <param name="Steps">Number of <see cref="BreedStep"/> nodes in the tree.</param>
/// <param name="ExpectedEggsTotal">Sum of the expected eggs of every step (steps run in order; expected values add up).</param>
/// <param name="Notes">Readable explanations as "key" or "key|argument" (see <see cref="PlanNotes"/>); the UI localizes them.</param>
public sealed record BreedingPlan(
    PlanNode Root, int Steps, double ExpectedEggsTotal, int WildCount,
    bool UsesStartParent, IReadOnlyList<string> PreferredUidsUsed, IReadOnlyList<string> Notes);

/// <summary>Keys of <see cref="BreedingPlan.Notes"/> and <see cref="PlanResult.Diagnostics"/>.</summary>
public static class PlanNotes
{
    public const string TargetOwned = "Plan.Note.TargetOwned";
    public const string SameSpeciesWild = "Plan.Note.SameSpeciesWild";
    public const string StartAssumed = "Plan.Note.StartAssumed";
    public const string NoPlan = "Plan.Diag.NoPlan";
    public const string WildOff = "Plan.Diag.WildOff";
    public const string StepLimit = "Plan.Diag.StepLimit";
    public const string WildLimit = "Plan.Diag.WildLimit";
    public const string StartUnused = "Plan.Diag.StartUnused";
    public const string PreferredUnused = "Plan.Diag.PreferredUnused";
    public const string NotUsable = "Plan.Reason.NotUsable";
    public const string TargetSameSpeciesOnly = "Plan.Diag.TargetSameSpeciesOnly";
    public const string LegendaryExcluded = "Plan.Diag.LegendaryExcluded";
    public const string PassiveMissing = "Plan.Diag.PassiveMissing";
    public const string TimedOut = "Plan.Diag.TimedOut";
    public const string TimedOutNoPlan = "Plan.Diag.TimedOutNoPlan";
    public const string StateLimit = "Plan.Diag.StateLimit";
    public const string TargetUnbreedable = "Plan.Diag.TargetUnbreedable";
    public const string BetterMayExist = "Plan.Diag.BetterMayExist";
    public const string StepsOnly = "Plan.Reason.StepLimit";
    /// <summary>"Plan.Note.FlexGender|intermediate species|backup partner species|backup partner gender (Male or Female)" (T105).</summary>
    public const string FlexGender = "Plan.Note.FlexGender";
}

/// <summary>Start parent: an owned Pal (fixed gender) or a species (both genders are tried). One of the two is set.</summary>
public sealed record StartParent(string? OwnedUid, string? SpeciesId, Gender? Gender);

/// <param name="ExcludeLegendaryWild">Species with rarity &gt;= 20 are not suggested as wild leaves (owned ones still count).</param>
/// <param name="TargetGender">Wanted gender of the Pal the last step produces (Any: it does not matter); it is part of the cost: the last step needs 1/p eggs.</param>
public sealed record PathQuery(
    string TargetId, StartParent? Start, IReadOnlyList<string> PreferredUids,
    bool AllowWild, int MaxWild, bool ExcludeLegendaryWild, int MaxSteps, int MaxResults, OwnerScope Scope,
    GenderReq TargetGender = GenderReq.Any, PreferredMode PreferredMode = PreferredMode.AtLeastOne,
    IReadOnlyList<string>? ExcludedUids = null, IReadOnlyList<string>? ExcludedSpecies = null, IReadOnlyList<string>? ExcludedGroups = null);

/// <param name="OwnedTargets">The Pals of the target species the user owns inside the scope (an information note, not a plan: the search goes on; ARCHITECTURE 7.3).</param>
/// <param name="OwnedMatching">Passive mode: how many of them already have the wanted passives (and gender).</param>
public sealed record PlanResult(
    IReadOnlyList<BreedingPlan> Plans, IReadOnlyList<PreferredFeasibility> Feasibility,
    IReadOnlyList<string> Diagnostics, bool TimedOut,
    IReadOnlyList<OwnedPal>? OwnedTargets = null, int OwnedMatching = 0, IReadOnlyList<ExclusionBlocker>? Blockers = null);

public sealed record PreferredFeasibility(string Uid, BreedingPlan? BestPlan, string? ReasonIfNone);

/// <param name="Expanded">States expanded so far (passive solver).</param>
/// <param name="BestCost">Lowest expected eggs of a plan found so far, when known.</param>
/// <param name="Partial">Best result so far: the finished fast stage, then every improvement of the second stage (anytime search).</param>
public sealed record SolverProgress(string Phase, double Fraction, long Expanded = 0, double? BestCost = null, PlanResult? Partial = null);

public enum PreferredMode { None, AtLeastOne }

/// <summary>Passive-oriented planner query (ARCHITECTURE 5, 7.4).</summary>
public sealed record GoalQuery(
    string TargetId, IReadOnlyList<string> DesiredPassives, int MaxUnwanted, GenderReq TargetGender,
    StartParent? Start, IReadOnlyList<string> PreferredUids, PreferredMode PreferredMode,
    bool AllowWild, int MaxWild, bool ExcludeLegendaryWild, int WildAssumedUnwanted, int MaxSteps,
    bool AllowSpecialCake, int MaxResults, OwnerScope Scope, TimeSpan TimeLimit,
    bool Exhaustive = false,
    IReadOnlyList<string>? ExcludedUids = null, IReadOnlyList<string>? ExcludedSpecies = null, IReadOnlyList<string>? ExcludedGroups = null);

public interface IPassiveSolver
{
    Task<PlanResult> SolveAsync(GoalQuery query, IReadOnlyList<OwnedPal> owned, CancellationToken ct, IProgress<SolverProgress>? progress = null);
}

public interface ISpeciesPathFinder
{
    Task<PlanResult> FindAsync(PathQuery query, IReadOnlyList<OwnedPal> owned, CancellationToken ct, IProgress<SolverProgress>? progress = null);
}
