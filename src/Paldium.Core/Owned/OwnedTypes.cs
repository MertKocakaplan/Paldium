using System.Text.Json.Serialization;
using Paldium.Core.Domain;

namespace Paldium.Core.Owned;

/// <summary>
/// A user correction applied on top of a Pal read from a source (ARCHITECTURE 4.7, 6.4). Every field is
/// optional: null means "keep the source value". The correction is stored by Pal uid and re-applied to every new
/// snapshot of that source.
/// </summary>
public sealed record PalOverride
{
    public Gender? Gender { get; init; }

    /// <summary>"The gender is unknown": the source's gender is dropped (<see cref="Gender"/> null alone means "no correction").</summary>
    public bool ClearGender { get; init; }

    public IReadOnlyList<string>? Passives { get; init; }
    public Ivs? Ivs { get; init; }

    /// <summary>"The IVs are unknown": the source's IVs are dropped (<see cref="Ivs"/> null alone means "no correction").</summary>
    public bool ClearIvs { get; init; }

    public int? Level { get; init; }

    /// <summary>The "do not use in planning" switch (<see cref="OwnedPal.IsExcluded"/>).</summary>
    public bool? IsExcluded { get; init; }

    public string? Note { get; init; }

    /// <summary>True when the override changes Pal data (not only the exclusion switch).</summary>
    [JsonIgnore]
    public bool HasCorrections => Gender != null || ClearGender || Passives != null || Ivs != null || ClearIvs || Level != null || Note != null;

    /// <summary>
    /// The correction that turns the RAW source Pal into the edited values (T62). A field equal to the raw value is no correction (so a value the user
    /// put back is dropped), a field the source has but the user cleared is written as "unknown" (<see cref="ClearGender"/>, <see cref="ClearIvs"/>).
    /// The exclusion switch and the note are kept from <paramref name="previous"/>.
    /// </summary>
    public static PalOverride Diff(OwnedPal raw, Gender? gender, int level, IReadOnlyList<string> passives, Ivs? ivs, PalOverride? previous = null) => new()
    {
        IsExcluded = previous?.IsExcluded,
        Note = previous?.Note,
        Gender = gender != raw.Gender ? gender : null,
        ClearGender = gender != raw.Gender && gender == null,
        Level = level != raw.Level ? level : null,
        Passives = passives.SequenceEqual(raw.Passives, StringComparer.Ordinal) ? null : passives,
        Ivs = ivs != raw.Ivs ? ivs : null,
        ClearIvs = ivs != raw.Ivs && ivs == null,
    };

    /// <summary>True when the override changes nothing and can be dropped.</summary>
    [JsonIgnore]
    public bool IsEmpty => !HasCorrections && IsExcluded == null;
}

/// <summary>Input for a manually added Pal; the repository validates it and assigns the uid.</summary>
public sealed record OwnedPalDraft
{
    public required string SpeciesId { get; init; }
    public Gender? Gender { get; init; }
    public int Level { get; init; } = 1;

    /// <summary>Condensation rank, 1..5.</summary>
    public int Rank { get; init; } = 1;

    public IReadOnlyList<string> Passives { get; init; } = [];
    public Ivs? Ivs { get; init; }
    public IReadOnlyList<string> EquippedAttacks { get; init; } = [];
    public IReadOnlyList<string> LearnedAttacks { get; init; } = [];
    public string? Nickname { get; init; }
    public PalLocation Location { get; init; } = new(LocationKind.Unknown, null, null, null);
    public bool IsExcluded { get; init; }
}

public static class SourceState
{
    public const string Idle = "Idle";
    public const string Reading = "Reading";
    public const string Ok = "Ok";
    public const string Error = "Error";
}

/// <summary>State of one Pal source (ARCHITECTURE 5). <see cref="State"/> is one of <see cref="SourceState"/>.</summary>
public sealed record SourceStatus(string SourceId, string State, DateTimeOffset? LastSyncUtc, int PalCount, string? Error);

/// <summary>Immutable view of all owned Pals after merging every source and applying the corrections.</summary>
public sealed record OwnedPalSnapshot(
    IReadOnlyList<OwnedPal> Pals,
    IReadOnlyList<SourceStatus> Sources,
    IReadOnlyDictionary<string, PalOverride> Overrides,
    IReadOnlyList<string> OrphanOverrideUids,
    int DuplicateCount,
    long Version)
{
    public static OwnedPalSnapshot Empty { get; } = new([], [], new Dictionary<string, PalOverride>(), [], 0, 0);

    /// <summary>
    /// The visible Pal a stored selection points to: a <see cref="OwnedPal.SelectionKey"/>, or an old selection that holds a uid
    /// (<c>save:world:instance</c>, <c>mod:instance</c>), which is matched by its instance id when the copy of that source is no longer the visible one.
    /// </summary>
    public OwnedPal? Find(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var found = Pals.FirstOrDefault(p => string.Equals(p.SelectionKey, key, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Uid, key, StringComparison.Ordinal));
        if (found != null) return found;
        var instance = key[(key.LastIndexOf(':') + 1)..];
        return instance.Length == 0 ? null : Pals.FirstOrDefault(p => string.Equals(p.InstanceId, instance, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Which of the user's Pals count (ARCHITECTURE 5, 6.2).</summary>
/// <param name="PlayerIds">Owners to include; null means every owner. Pals without an owner (manual entries) always count.</param>
/// <param name="IncludedLocations">Location kinds to include.</param>
/// <param name="IncludeExcluded">Include Pals marked "do not use in planning".</param>
/// <param name="OwnerFreeLocations">Location kinds whose Pals count whoever owns them (Base and the Dimensional Pal Storage are shared by the guild).</param>
public sealed record OwnerScope(
    IReadOnlyList<string>? PlayerIds,
    IReadOnlyList<LocationKind> IncludedLocations,
    bool IncludeExcluded = false,
    IReadOnlyList<LocationKind>? OwnerFreeLocations = null)
{
    /// <summary>Every location kind.</summary>
    public static IReadOnlyList<LocationKind> AllLocations { get; } = Enum.GetValues<LocationKind>();

    public static OwnerScope Everything { get; } = new(null, AllLocations);
}

/// <summary>Validation failure with a stable key the UI can localize.</summary>
public sealed class OwnedPalValidationException : ArgumentException
{
    public const string UnknownSpecies = "Owned.Error.UnknownSpecies";
    public const string UnknownPassive = "Owned.Error.UnknownPassive";
    public const string TooManyPassives = "Owned.Error.TooManyPassives";
    public const string DuplicatePassive = "Owned.Error.DuplicatePassive";
    public const string IvRange = "Owned.Error.IvRange";
    public const string LevelRange = "Owned.Error.LevelRange";
    public const string RankRange = "Owned.Error.RankRange";
    public const string NotManual = "Owned.Error.NotManual";

    public OwnedPalValidationException(string key, string detail)
        : base($"{key}: {detail}")
    {
        Key = key;
        Detail = detail;
    }

    /// <summary>Localization key, one of the constants above.</summary>
    public string Key { get; }

    /// <summary>Technical detail (offending value), not meant for the user.</summary>
    public string Detail { get; }
}
