using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.App.Services;

public sealed record PairResolution(
    OwnedPal? OwnedA,
    OwnedPal? OwnedB,
    PalSpecies? SpeciesA,
    PalSpecies? SpeciesB,
    IReadOnlyList<ChildOutcome> MatchingOutcomes,
    PalSpecies? FallbackChild,
    bool SameGender,
    Gender? ConflictingGender,
    bool InvalidPair,
    PalSpecies? ValidOwnedChild);

/// <summary>
/// Holds the active Calculator tab, inputs, and Passive Odds side-panel options in memory for the lifetime
/// of the app session (screen: calculator.md §Etkileşimler; never persisted to disk).
/// </summary>
public sealed class CalculatorStateService
{
    public const string ModePair = "cift";
    public const string ModeParents = "ebeveynler";
    public const string ModeOdds = "olasilik";
    public const string ModeMutation = "mutasyon";

    public const string CakeNone = "none";
    public const string CakeNormal = "normal";
    public const string CakeSpecial = "special";

    private string? _lastAppliedQueryKey;

    public string Mode { get; set; } = ModePair;

    // Parent A & B inputs shared by Pair -> Child and Mutation workbench modes.
    public bool OwnedModeA { get; set; }
    public bool OwnedModeB { get; set; }
    public string? SpeciesAId { get; set; }
    public string? SpeciesBId { get; set; }
    public string? OwnedAKey { get; set; }
    public string? OwnedBKey { get; set; }

    /// <summary>Selected cake in the workbench bottom strip: "none", "normal", or "special".</summary>
    public string Cake { get; set; } = CakeNone;

    /// <summary>Target species id for "Hedef -> ebeveynler" (used when navigating from Paldex or ChildNode).</summary>
    public string? TargetSpeciesId { get; set; }

    public int? TargetFilter { get; set; } // null = auto (1 if any owned pairs else 0); 0 = All, 1 = Any owned, 2 = Both owned
    public string TargetSearch { get; set; } = string.Empty;

    // PassiveOddsPanel state (when both A and B are owned Pals).
    public string? PairOddsAutoFillKey { get; set; }
    public List<string> PairDesiredPassives { get; set; } = [];
    public int PairMaxUnwanted { get; set; }
    public GenderReq PairTargetGender { get; set; } = GenderReq.Any;

    // PassiveOdds (free-entry calculator tab) state.
    public List<string> OddsPassivesA { get; set; } = [];
    public List<string> OddsPassivesB { get; set; } = [];
    public int OddsUnknownA { get; set; }
    public int OddsUnknownB { get; set; }
    public List<string> OddsDesired { get; set; } = [];
    public int OddsMaxUnwanted { get; set; }
    public bool OddsSpecialCake { get; set; }

    // Mutation reverse-lookup tab state.
    public string? MutationReverseTargetId { get; set; }
    public bool MutationOwnedOnly { get; set; }

    public static string NormalizeMode(string? raw) => (raw ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        ModePair or "pair" => ModePair,
        ModeParents or "parents" or "target" => ModeParents,
        ModeOdds or "odds" => ModeOdds,
        ModeMutation or "mutation" => ModeMutation,
        _ => ModePair,
    };

    public string? SpecialCakeId => Cake == CakeSpecial ? "Cake05" : null;

    public string? MutationCakeId => Cake == CakeNormal ? "Cake04" : null;

    /// <summary>
    /// Applies query parameters (<c>?mode=&amp;a=&amp;b=&amp;target=&amp;passive=</c>) once per unique navigation signature.
    /// Owned UID/SelectionKey takes precedence over species id when matching <paramref name="a"/> or <paramref name="b"/>.
    /// </summary>
    public bool ApplyQuery(
        string? mode,
        string? a,
        string? b,
        string? target,
        IGameData data,
        IOwnedPalRepository repository,
        string? passive = null)
    {
        var hasMode = !string.IsNullOrWhiteSpace(mode);
        var hasA = !string.IsNullOrWhiteSpace(a);
        var hasB = !string.IsNullOrWhiteSpace(b);
        var hasTarget = !string.IsNullOrWhiteSpace(target);
        var hasPassive = !string.IsNullOrWhiteSpace(passive);
        if (!hasMode && !hasA && !hasB && !hasTarget && !hasPassive) return false;

        var key = $"{mode}|{a}|{b}|{target}|{passive}";
        if (string.Equals(key, _lastAppliedQueryKey, StringComparison.Ordinal)) return false;
        _lastAppliedQueryKey = key;

        if (hasA && hasB)
        {
            ApplyParent(isA: true, a!, data, repository);
            ApplyParent(isA: false, b!, data, repository);
            Mode = hasMode ? NormalizeMode(mode) : ModePair;
        }
        else if (hasA)
        {
            ApplyParent(isA: true, a!, data, repository);
            if (hasMode) Mode = NormalizeMode(mode);
        }
        else if (hasB)
        {
            ApplyParent(isA: false, b!, data, repository);
            if (hasMode) Mode = NormalizeMode(mode);
        }

        if (hasTarget && data.TryNormalizePalId(target!, out var targetId))
        {
            TargetSpeciesId = targetId;
            if (!(hasA && hasB))
                Mode = hasMode ? NormalizeMode(mode) : ModeParents;
        }

        if (hasPassive)
        {
            var matchedPassive = data.Passives.FirstOrDefault(p =>
                string.Equals(p.Id, passive, StringComparison.OrdinalIgnoreCase));
            if (matchedPassive != null)
            {
                OddsDesired = [matchedPassive.Id];
                Mode = ModeOdds;
            }
            else if (hasMode)
            {
                Mode = NormalizeMode(mode);
            }
        }
        else if (hasMode && !hasA && !hasB && !hasTarget)
        {
            Mode = NormalizeMode(mode);
        }

        return true;
    }

    public void SwapParents()
    {
        (OwnedModeA, OwnedModeB) = (OwnedModeB, OwnedModeA);
        (SpeciesAId, SpeciesBId) = (SpeciesBId, SpeciesAId);
        (OwnedAKey, OwnedBKey) = (OwnedBKey, OwnedAKey);
    }

    public void SetOwnedMode(bool isA, bool owned)
    {
        if (isA)
        {
            if (OwnedModeA == owned) return;
            OwnedModeA = owned;
            OwnedAKey = null;
            SpeciesAId = null;
        }
        else
        {
            if (OwnedModeB == owned) return;
            OwnedModeB = owned;
            OwnedBKey = null;
            SpeciesBId = null;
        }
    }

    public void SetSpecies(bool isA, PalSpecies? pal)
    {
        if (isA) SpeciesAId = pal?.Id;
        else SpeciesBId = pal?.Id;
    }

    public void SetOwned(bool isA, OwnedPal? pal)
    {
        if (isA)
        {
            OwnedAKey = pal?.SelectionKey;
            SpeciesAId = pal?.SpeciesId;
        }
        else
        {
            OwnedBKey = pal?.SelectionKey;
            SpeciesBId = pal?.SpeciesId;
        }
    }

    /// <summary>
    /// Opens the pair in owned mode with one Pal of each species selected (the best by passives, then level), for a pair where both species are
    /// owned. A parent needs the gender the recipe asks for. False when a parent has no fitting Pal (the caller opens the species pair instead).
    /// </summary>
    public bool TryOpenOwnedPair(string speciesAId, GenderReq aReq, string speciesBId, GenderReq bReq, IEnumerable<OwnedPal> pals)
    {
        var list = pals.ToList();
        OwnedPal? Best(string speciesId, GenderReq req, string? exceptUid) => list
            .Where(p => string.Equals(p.SpeciesId, speciesId, StringComparison.OrdinalIgnoreCase) && p.Uid != exceptUid)
            .Where(p => req == GenderReq.Any || (req == GenderReq.Male ? p.Gender == Gender.Male : p.Gender == Gender.Female))
            .OrderByDescending(p => p.Passives.Count)
            .ThenByDescending(p => p.Level)
            .ThenBy(p => p.Uid, StringComparer.Ordinal)
            .FirstOrDefault();

        var a = Best(speciesAId, aReq, null);
        var b = a == null ? null : Best(speciesBId, bReq, a.Uid);
        if (a == null || b == null) return false;
        OwnedModeA = true;
        OwnedModeB = true;
        SetOwned(true, a);
        SetOwned(false, b);
        return true;
    }

    public void SwitchPairToSpeciesMode(OwnedPal? ownedA, OwnedPal? ownedB)
    {
        if (ownedA != null) SpeciesAId = ownedA.SpeciesId;
        if (ownedB != null) SpeciesBId = ownedB.SpeciesId;
        OwnedModeA = false;
        OwnedModeB = false;
        OwnedAKey = null;
        OwnedBKey = null;
    }

    public void SyncPairDesiredFromParents(OwnedPal a, OwnedPal b, IGameData data)
    {
        var pairKey = $"{a.SelectionKey}|{b.SelectionKey}";
        if (string.Equals(PairOddsAutoFillKey, pairKey, StringComparison.Ordinal)) return;
        PairOddsAutoFillKey = pairKey;
        PairDesiredPassives = a.Passives
            .Concat(b.Passives)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id =>
            {
                try { return data.Passive(id).Rank > 0; }
                catch (KeyNotFoundException) { return false; }
            })
            .Take(data.Mechanics.MaxPassives)
            .ToList();
    }

    public PairResolution ResolvePair(IGameData data, IBreedingCalculator breeding, IOwnedPalRepository repository)
    {
        var ownedA = OwnedModeA && !string.IsNullOrEmpty(OwnedAKey) ? repository.Current.Find(OwnedAKey) : null;
        var ownedB = OwnedModeB && !string.IsNullOrEmpty(OwnedBKey) ? repository.Current.Find(OwnedBKey) : null;

        var speciesA = OwnedModeA
            ? (ownedA != null ? TryPal(data, ownedA.SpeciesId) : null)
            : (!string.IsNullOrEmpty(SpeciesAId) ? TryPal(data, SpeciesAId) : null);
        var speciesB = OwnedModeB
            ? (ownedB != null ? TryPal(data, ownedB.SpeciesId) : null)
            : (!string.IsNullOrEmpty(SpeciesBId) ? TryPal(data, SpeciesBId) : null);

        var genderA = OwnedModeA && ownedA != null ? ToReq(ownedA.Gender) : GenderReq.Any;
        var genderB = OwnedModeB && ownedB != null ? ToReq(ownedB.Gender) : GenderReq.Any;
        var sameGender = genderA != GenderReq.Any && genderA == genderB;
        var conflictingGender = sameGender ? ownedA?.Gender : null;

        if (speciesA == null || speciesB == null)
        {
            return new PairResolution(ownedA, ownedB, speciesA, speciesB, [], null, sameGender, conflictingGender, false, null);
        }

        var allOutcomes = breeding.GetChildOutcomes(speciesA.Id, speciesB.Id);
        var matching = allOutcomes
            .Where(o => MatchesGender(o.AGender, genderA) && MatchesGender(o.BGender, genderB))
            .ToList();
        var fallbackChild = TryPal(data, breeding.GetChild(speciesA.Id, speciesB.Id));
        var invalidPair = sameGender || matching.Count == 0;
        var validOwnedChild = !invalidPair && OwnedModeA && OwnedModeB && ownedA != null && ownedB != null && matching.Count > 0
            ? TryPal(data, matching[0].ChildId)
            : null;

        return new PairResolution(ownedA, ownedB, speciesA, speciesB, matching, fallbackChild, sameGender, conflictingGender, invalidPair, validOwnedChild);
    }

    private void ApplyParent(bool isA, string raw, IGameData data, IOwnedPalRepository repository)
    {
        var owned = repository.Current.Find(raw);
        if (owned != null)
        {
            if (isA)
            {
                OwnedModeA = true;
                OwnedAKey = owned.SelectionKey;
                SpeciesAId = owned.SpeciesId;
            }
            else
            {
                OwnedModeB = true;
                OwnedBKey = owned.SelectionKey;
                SpeciesBId = owned.SpeciesId;
            }
            return;
        }

        if (data.TryNormalizePalId(raw, out var speciesId))
        {
            if (isA)
            {
                OwnedModeA = false;
                OwnedAKey = null;
                SpeciesAId = speciesId;
            }
            else
            {
                OwnedModeB = false;
                OwnedBKey = null;
                SpeciesBId = speciesId;
            }
        }
    }

    private static GenderReq ToReq(Gender? gender) => gender switch
    {
        Gender.Male => GenderReq.Male,
        Gender.Female => GenderReq.Female,
        _ => GenderReq.Any,
    };

    private static bool MatchesGender(GenderReq required, GenderReq chosen) =>
        required == GenderReq.Any || chosen == GenderReq.Any || required == chosen;

    private static PalSpecies? TryPal(IGameData data, string id)
    {
        try { return data.Pal(id); }
        catch (KeyNotFoundException) { return null; }
    }
}
