using System.Globalization;
using Microsoft.Extensions.Localization;
using Paldium.App.Components;
using Paldium.App.Resources;
using Paldium.App.Services;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;

namespace Paldium.App.Pages.PlannerParts;

/// <summary>Texts, step helpers, and structured notes of breeding plans.</summary>
public static class PlanText
{
    public sealed record PlanNoteEntry(string Text, BreedingPlan? SelectPlan = null, string? SelectLabel = null);

    public sealed record DiagnosticEntry(
        string Text,
        string Related,
        string? ActionLabel,
        string? FlagControl,
        Func<PlannerSettings, PlannerSettings>? ApplyFix);

    public sealed record OutOfScopeLeaf(int StepNumber, OwnedPal Pal, string DisplayName);

    /// <summary>What the Planner canvas shows.</summary>
    public enum CanvasView { Skeleton, Tree, Diagnosis, Input }

    /// <summary>
    /// Picks the canvas view: the skeleton while the first search runs, the tree when there is a plan, the diagnosis only for a real empty result
    /// and the input view (start Pal and target) otherwise, also while "Edit target" is on.
    /// </summary>
    public static CanvasView ChooseCanvas(bool computing, bool hasPlan, bool editingInputs, bool emptyResult, bool hasTarget)
    {
        if (computing && !hasPlan) return CanvasView.Skeleton;
        if (hasPlan && !editingInputs) return CanvasView.Tree;
        if (emptyResult && !computing && hasTarget) return CanvasView.Diagnosis;
        return CanvasView.Input;
    }

    /// <summary>Where an owned Pal is (the shared <see cref="LocationText"/>).</summary>
    public static string Location(OwnedPal pal, GameTerms terms, IStringLocalizer<Strings> l) => LocationText.Format(pal, terms, l);

    /// <summary>"Yakala", "Kafesten al", ... for a wild leaf.</summary>
    public static string ObtainLabel(string method, IStringLocalizer<Strings> l) =>
        l["Plan.Obtain." + method] is { ResourceNotFound: false } text ? text.Value : method;

    /// <summary>Localizes a "key" or "key|argument" note / diagnostic.</summary>
    public static string Note(string note, IStringLocalizer<Strings> l, Func<string, string> speciesName, string? lang = null)
    {
        if (note.StartsWith(PlanNotes.FlexGender + "|", StringComparison.Ordinal))
        {
            // "Plan.Note.FlexGender|intermediate|backup partner species|Male or Female" (T105)
            var f = note.Split('|');
            var template = l[PlanNotes.FlexGender];
            if (!template.ResourceNotFound && f.Length == 4)
            {
                var word = l[f[3] == "Male" ? "Common.MaleLower" : "Common.FemaleLower"].Value;
                return string.Format(string.IsNullOrEmpty(lang) ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(lang), template.Value,
                    speciesName(f[1]), speciesName(f[2]), word);
            }
        }

        var parts = note.Split('|', 2);
        var text = l[parts[0]];
        if (text.ResourceNotFound) return note;
        var culture = string.IsNullOrEmpty(lang) ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(lang);
        return parts.Length == 2 ? string.Format(culture, text.Value, speciesName(parts[1])) : text.Value;
    }

    /// <summary>
    /// The two parents of a step as they are used (T105): with a backup partner the flexible parent's gender decides which partner pairs with it.
    /// <paramref name="flexChoices"/> holds the gender that hatched, by step index; without a choice the main partner is used.
    /// </summary>
    public static (PlanNode A, PlanNode B) ParentsInUse(BreedingPlan plan, BreedStep step, IReadOnlyDictionary<int, Gender> flexChoices)
    {
        if (step.AltPartner == null) return (step.ParentA, step.ParentB);
        var flexible = step.ParentA is BreedStep { ChildGender: GenderReq.Any } ? step.ParentA : step.ParentB;
        var partner = ReferenceEquals(flexible, step.ParentA) ? step.ParentB : step.ParentA;
        var steps = StepsOf(plan);
        var index = -1;
        for (var i = 0; i < steps.Count; i++) if (ReferenceEquals(steps[i], flexible)) { index = i; break; }
        if (index < 0 || !flexChoices.TryGetValue(index, out var hatched)) return (step.ParentA, step.ParentB);

        var partnerGender = partner is OwnedLeaf o ? o.Pal.Gender : partner.Gender == GenderReq.Male ? Gender.Male : partner.Gender == GenderReq.Female ? Gender.Female : (Gender?)null;
        // The main partner pairs with the opposite gender; the other gender pairs with the backup.
        var usesMain = partnerGender == null || hatched != partnerGender;
        var used = usesMain ? partner : step.AltPartner;
        return ReferenceEquals(flexible, step.ParentA) ? (flexible, used) : (used, flexible);
    }

    /// <summary>Collects steps in execution order (children before the steps that use them).</summary>
    public static IReadOnlyList<BreedStep> StepsOf(BreedingPlan plan)
    {
        var steps = new List<BreedStep>();
        void Walk(PlanNode node)
        {
            if (node is not BreedStep step) return;
            Walk(step.ParentA);
            Walk(step.ParentB);
            steps.Add(step);
        }

        Walk(plan.Root);
        return steps;
    }

    /// <summary>Index of the first step not marked done, or -1 if all steps are done.</summary>
    public static int CurrentStepIndex(IReadOnlyList<BreedStep> steps, IReadOnlySet<int> done)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (!done.Contains(i)) return i;
        }
        return -1;
    }

    /// <summary>Builds all notes for the selected plan (owned target note, solver notes, unused preferred Pals).</summary>
    public static IReadOnlyList<PlanNoteEntry> BuildNotes(
        PlanResult result,
        BreedingPlan plan,
        PlannerSettings query,
        IOwnedPalRepository repository,
        IGameData data,
        GameTerms terms,
        IStringLocalizer<Strings> l,
        string lang)
    {
        var list = new List<PlanNoteEntry>();
        var culture = CultureInfo.GetCultureInfo(lang);
        string SpeciesName(string id)
        {
            try { return data.Pal(id).DisplayName(lang); }
            catch (KeyNotFoundException) { return id; }
        }

        if (result.OwnedTargets is { Count: > 0 } ownedTargets)
        {
            var targetName = SpeciesName(plan.Root.SpeciesId);
            var firstPlace = LocationText.ShortPlace(ownedTargets[0], terms, l);
            var text = l["Plan.OwnedNoteCompact", targetName, ownedTargets.Count, firstPlace].Value;
            if (result.OwnedMatching > 0)
                text += " " + l["Plan.OwnedMatching", result.OwnedMatching].Value;
            list.Add(new PlanNoteEntry(text));
        }

        foreach (var raw in plan.Notes)
            list.Add(new PlanNoteEntry(Note(raw, l, SpeciesName, lang)));

        // The search ran out of time: say so (T100); the button "Keep searching" is on the plan strip.
        if (result.TimedOut)
            list.Add(new PlanNoteEntry(l["Plan.NoteTimedOut"].Value));

        var usedPrefs = new HashSet<string>(plan.PreferredUidsUsed, StringComparer.Ordinal);
        foreach (var uid in query.PreferredUids)
        {
            var pal = repository.Current.Find(uid);
            var resolvedUid = pal?.Uid ?? uid;
            if (usedPrefs.Contains(resolvedUid)) continue;

            var palName = pal != null ? (pal.Nickname ?? SpeciesName(pal.SpeciesId)) : uid;
            var feas = result.Feasibility.FirstOrDefault(f => string.Equals(f.Uid, resolvedUid, StringComparison.Ordinal));
            if (feas?.ReasonIfNone != null)
            {
                var reasonText = l[feas.ReasonIfNone] is { ResourceNotFound: false } rt ? rt.Value : feas.ReasonIfNone;
                list.Add(new PlanNoteEntry(l["Plan.NotePreferredReason", palName, reasonText].Value));
            }
            else if (feas?.BestPlan is { } altPlan)
            {
                var eggs = "~" + altPlan.ExpectedEggsTotal.ToString("0.0", culture);
                list.Add(new PlanNoteEntry(l["Plan.NotePreferredNotBest", palName, altPlan.Steps, eggs].Value, altPlan, palName));
            }
            else
            {
                list.Add(new PlanNoteEntry(l["Plan.NotePreferredUnused", palName].Value));
            }
        }

        return list;
    }

    /// <summary>The 1-based number of a step in the order of the checklist (children before the steps that use them); 0 when it is not a step of the plan.</summary>
    public static int StepNumber(BreedingPlan plan, BreedStep step)
    {
        var number = 0;
        var found = 0;
        void Walk(PlanNode node)
        {
            if (node is not BreedStep s) return;
            Walk(s.ParentA);
            Walk(s.ParentB);
            number++;
            if (ReferenceEquals(s, step)) found = number;
        }

        Walk(plan.Root);
        return found;
    }

    /// <summary>The leaf parents of a plan (the Pals that are not made in a step): species and gender, each once, in the order of the tree.</summary>
    public static IReadOnlyList<(string SpeciesId, Gender? Gender)> Leaves(BreedingPlan plan)
    {
        var result = new List<(string, Gender?)>();
        void Walk(PlanNode node)
        {
            switch (node)
            {
                case BreedStep step:
                    Walk(step.ParentA);
                    Walk(step.ParentB);
                    break;
                case OwnedLeaf owned:
                    Add(owned.SpeciesId, owned.Pal.Gender);
                    break;
                case WildLeaf wild:
                    Add(wild.SpeciesId, wild.Gender switch { GenderReq.Male => Gender.Male, GenderReq.Female => Gender.Female, _ => null });
                    break;
            }
        }

        void Add(string species, Gender? gender)
        {
            if (!result.Contains((species, gender))) result.Add((species, gender));
        }

        Walk(plan.Root);
        return result;
    }

    /// <summary>Builds actionable diagnostics for the "no plan found" panel.</summary>
    public static IReadOnlyList<DiagnosticEntry> BuildDiagnostics(
        PlanResult result,
        PlannerSettings query,
        IGameData data,
        IStringLocalizer<Strings> l,
        string lang,
        Func<PlanExclusion, string>? exclusionName = null)
    {
        var list = new List<DiagnosticEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string SpeciesName(string id)
        {
            try { return data.Pal(id).DisplayName(lang); }
            catch (KeyNotFoundException) { return id; }
        }
        string PassiveName(string id)
        {
            try { return data.Passive(id).Names.Get(lang); }
            catch (KeyNotFoundException) { return id; }
        }

        void AddEntry(string key, DiagnosticEntry entry)
        {
            if (seen.Add(key)) list.Add(entry);
        }

        // T107: exclusions that block the plan come first, each with the best plan without it; the other exclusions get one closing sentence.
        string NameOfExclusion(PlanExclusion e)
        {
            if (exclusionName != null) return exclusionName(e);
            if (e.Kind == PlanExclusion.GroupKind)
                return l["Plan.Exclude.GroupTitle", l[$"Plan.Exclude.GroupName.{e.Id}"] is { ResourceNotFound: false } g ? g.Value : e.Id].Value;
            return e.Kind == PlanExclusion.SpeciesKind ? SpeciesName(e.Id) : e.Id;
        }

        foreach (var blocker in result.Blockers ?? [])
        {
            var name = NameOfExclusion(blocker.Exclusion);
            var captured = blocker.Exclusion;
            var accusative = ExclusionOps.Accusative(name, lang);
            AddEntry("x:" + captured.Kind + captured.Id, new DiagnosticEntry(
                l[captured.Kind == PlanExclusion.GroupKind ? "Plan.Diag.ExcludedPlural" : "Plan.Diag.Excluded", name].Value,
                l["Plan.Diag.ExcludedRelated", name, "~" + blocker.Eggs.ToString("0.0", CultureInfo.GetCultureInfo(lang)), blocker.Steps].Value,
                l["Plan.Exclude.Restore", accusative].Value,
                null,
                q => ExclusionOps.Without(q, captured)));
        }

        // The exclusions that are not what blocks the plan get one closing sentence (T114).
        if (list.Count > 0 && result.Blockers is { Count: > 0 } blocked)
        {
            var others = PlanExclusions.ListOf(query.ExcludedUids, query.ExcludedSpecies, query.ExcludedGroups)
                .Where(e => !blocked.Any(b => b.Exclusion == e)).Select(NameOfExclusion).ToList();
            if (others.Count > 0)
                list.Add(new DiagnosticEntry(l[others.Count > 1 || others.Any(n => n.EndsWith("Pals", StringComparison.Ordinal)) ? "Plan.Diag.ExcludedNotBlockingPlural" : "Plan.Diag.ExcludedNotBlocking", string.Join(", ", others)].Value, string.Empty, null, null, null));
        }

        foreach (var raw in result.Diagnostics)
        {
            var parts = raw.Split('|', 2);
            var code = parts[0];
            var arg = parts.Length == 2 ? parts[1] : null;
            if (code == PlanNotes.NoPlan) continue;

            if (code == PlanNotes.WildOff)
            {
                AddEntry(raw, new DiagnosticEntry(
                    l[PlanNotes.WildOff].Value,
                    l["Plan.DiagRelated", l["Plan.WildPalsShort"].Value, l["Common.None"].Value].Value,
                    l["Plan.DiagAction.AllowWild"].Value,
                    "wild",
                    q => q with { AllowWild = true, MaxWild = Math.Max(1, q.MaxWild) }));
            }
            else if (code == PlanNotes.StepLimit)
            {
                var next = query.MaxSteps switch { < 6 => 6, < 8 => 8, _ => 10 };
                var canIncrease = query.MaxSteps < 10;
                AddEntry(raw, new DiagnosticEntry(
                    l[PlanNotes.StepLimit].Value,
                    l["Plan.DiagRelated", l["Plan.MaxSteps"].Value, query.MaxSteps].Value,
                    canIncrease ? l["Plan.DiagAction.StepLimit", next].Value : null,
                    "steps",
                    canIncrease ? q => q with { MaxSteps = next } : null));
            }
            else if (code == PlanNotes.WildLimit)
            {
                var nextWild = query.MaxWild < 2 ? 2 : SettingsPlannerOptions.UnlimitedWild;
                var curLabel = query.MaxWild >= 3 ? "3+" : query.MaxWild.ToString(CultureInfo.InvariantCulture);
                var nextLabel = nextWild >= 3 ? "3+" : nextWild.ToString(CultureInfo.InvariantCulture);
                var canIncrease = query.AllowWild && query.MaxWild < 3;
                AddEntry(raw, new DiagnosticEntry(
                    l[PlanNotes.WildLimit].Value,
                    l["Plan.DiagRelated", l["Plan.WildPalsShort"].Value, curLabel].Value,
                    canIncrease ? l["Plan.DiagAction.WildLimit", nextLabel].Value : null,
                    "wild",
                    canIncrease ? q => q with { AllowWild = true, MaxWild = nextWild } : null));
            }
            else if (code == PlanNotes.PassiveMissing && arg != null)
            {
                AddEntry(raw, new DiagnosticEntry(
                    l[PlanNotes.PassiveMissing, PassiveName(arg)].Value,
                    l["Plan.DiagRelatedShelfScope"].Value,
                    l["Plan.DiagAction.RemovePassive"].Value,
                    "scope",
                    q => q with
                    {
                        DesiredPassives = q.DesiredPassives.Where(p => !string.Equals(p, arg, StringComparison.OrdinalIgnoreCase)).ToList(),
                    }));
            }
            else if (code == PlanNotes.LegendaryExcluded && arg != null)
            {
                AddEntry(raw, new DiagnosticEntry(
                    l[PlanNotes.LegendaryExcluded, SpeciesName(arg)].Value,
                    l["Plan.DiagRelated", l["Plan.LegendaryWildShort"].Value, l["Plan.Skip"].Value].Value,
                    l["Plan.DiagAction.AllowLegendary"].Value,
                    "legendary",
                    q => q with { ExcludeLegendaryWild = false }));
            }
            else if (code == PlanNotes.StartUnused)
            {
                AddEntry(raw, new DiagnosticEntry(
                    l[PlanNotes.StartUnused].Value,
                    l["Plan.DiagRelatedStart"].Value,
                    l["Plan.DiagAction.ClearStart"].Value,
                    null,
                    q => q with { StartMode = "none", StartOwnedUid = null, StartSpeciesId = null }));
            }
            else if (code == PlanNotes.PreferredUnused)
            {
                AddEntry(raw, new DiagnosticEntry(
                    l[PlanNotes.PreferredUnused].Value,
                    l["Plan.DiagRelatedPreferred"].Value,
                    l["Plan.DiagAction.ClearPreferredReq"].Value,
                    null,
                    q => q with { RequirePreferred = false }));
            }
            else if (code == PlanNotes.TargetSameSpeciesOnly && arg != null)
            {
                var rel = query.AllowWild
                    ? l["Plan.DiagRelatedShelfScope"].Value
                    : l["Plan.DiagRelated", l["Plan.WildPalsShort"].Value, l["Common.None"].Value].Value;
                AddEntry(raw, new DiagnosticEntry(
                    l[PlanNotes.TargetSameSpeciesOnly, SpeciesName(arg)].Value,
                    rel,
                    query.AllowWild ? null : l["Plan.DiagAction.AllowWild"].Value,
                    query.AllowWild ? "scope" : "wild",
                    query.AllowWild ? null : q => q with { AllowWild = true, MaxWild = Math.Max(1, q.MaxWild) }));
            }
            else if (code == PlanNotes.TargetUnbreedable && arg != null)
            {
                var rel = query.AllowWild
                    ? l["Plan.DiagRelatedShelfScope"].Value
                    : l["Plan.DiagRelated", l["Plan.WildPalsShort"].Value, l["Common.None"].Value].Value;
                AddEntry(raw, new DiagnosticEntry(
                    l[PlanNotes.TargetUnbreedable, SpeciesName(arg)].Value,
                    rel,
                    query.AllowWild ? null : l["Plan.DiagAction.AllowWild"].Value,
                    query.AllowWild ? "scope" : "wild",
                    query.AllowWild ? null : q => q with { AllowWild = true, MaxWild = Math.Max(1, q.MaxWild) }));
            }
            else
            {
                AddEntry(raw, new DiagnosticEntry(
                    Note(raw, l, SpeciesName, lang),
                    l["Plan.DiagRelatedShelfScope"].Value,
                    null,
                    null,
                    null));
            }
        }

        if (list.Count == 0 && result.Plans.Count == 0)
        {
            if (!query.AllowWild)
            {
                AddEntry("fallback-wild", new DiagnosticEntry(
                    l[PlanNotes.WildOff].Value,
                    l["Plan.DiagRelated", l["Plan.WildPalsShort"].Value, l["Common.None"].Value].Value,
                    l["Plan.DiagAction.AllowWild"].Value,
                    "wild",
                    q => q with { AllowWild = true, MaxWild = Math.Max(1, q.MaxWild) }));
            }
            if (query.MaxSteps < 10)
            {
                var next = query.MaxSteps switch { < 6 => 6, < 8 => 8, _ => 10 };
                AddEntry("fallback-steps", new DiagnosticEntry(
                    l[PlanNotes.StepLimit].Value,
                    l["Plan.DiagRelated", l["Plan.MaxSteps"].Value, query.MaxSteps].Value,
                    l["Plan.DiagAction.StepLimit", next].Value,
                    "steps",
                    q => q with { MaxSteps = next }));
            }
            if (list.Count == 0)
            {
                AddEntry("fallback-noplan", new DiagnosticEntry(
                    l[PlanNotes.NoPlan].Value,
                    l["Plan.DiagRelatedShelfScope"].Value,
                    null,
                    "scope",
                    null));
            }
        }

        return list;
    }

    /// <summary>Lists leaf Pals of the current plan that are no longer in the active scope, paired with their step number.</summary>
    public static IReadOnlyList<OutOfScopeLeaf> FindOutOfScopeLeaves(
        BreedingPlan? plan,
        IReadOnlySet<string> scopedUids,
        IGameData data,
        string lang)
    {
        if (plan == null) return [];
        var list = new List<OutOfScopeLeaf>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stepIdx = 0;

        string PalName(OwnedPal pal)
        {
            if (!string.IsNullOrWhiteSpace(pal.Nickname)) return pal.Nickname;
            try { return data.Pal(pal.SpeciesId).DisplayName(lang); }
            catch (KeyNotFoundException) { return pal.SpeciesId; }
        }

        void Walk(PlanNode node)
        {
            if (node is not BreedStep step) return;
            Walk(step.ParentA);
            Walk(step.ParentB);
            stepIdx++;
            if (step.ParentA is OwnedLeaf oa && !scopedUids.Contains(oa.Pal.Uid) && seen.Add(oa.Pal.Uid))
                list.Add(new OutOfScopeLeaf(stepIdx, oa.Pal, PalName(oa.Pal)));
            if (step.ParentB is OwnedLeaf ob && !scopedUids.Contains(ob.Pal.Uid) && seen.Add(ob.Pal.Uid))
                list.Add(new OutOfScopeLeaf(stepIdx, ob.Pal, PalName(ob.Pal)));
        }

        Walk(plan.Root);
        return list;
    }

    /// <summary>"IV 85/92/70" for a Pal whose IVs are known; null otherwise.</summary>
    public static string? IvText(OwnedPal pal) => pal.Ivs is { } ivs ? $"IV {ivs.Hp}/{ivs.Attack}/{ivs.Defense}" : null;
}
