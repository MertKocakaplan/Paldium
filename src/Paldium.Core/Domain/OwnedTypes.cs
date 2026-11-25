using System.Text.Json.Serialization;
namespace Paldium.Core.Domain;

public enum PalSourceKind { Save, GlobalStorage, ModBridge, Manual }

public enum LocationKind { Party, Palbox, Base, ViewingCage, DimensionalStorage, GlobalStorage, Unknown }

/// <summary>Talent values 0..100.</summary>
public sealed record Ivs(int Hp, int Attack, int Defense);

public sealed record PalOwner(string PlayerId, string? PlayerName, string? GuildId, string? GuildName);

/// <summary>
/// Where a Pal is. <see cref="Page"/> (0 based) and <see cref="SlotIndex"/> (0-29, inside the page) are set the same way by every source for the
/// Pal Box and the Dimensional Pal Storage; <see cref="ContainerId"/> only tells containers apart (the storage box id or owner), it is never a page.
/// Party and bases have no pages: their slot is the slot of the container. <see cref="BaseName"/> is the base's name when the source knows it (the mod).
/// </summary>
public sealed record PalLocation(LocationKind Kind, string? ContainerId, int? SlotIndex, string? BaseId, int? Page = null, string? BaseName = null)
{
    /// <summary>Slots of one page of the Pal Box and of the Dimensional Pal Storage.</summary>
    public const int PageSize = 30;

    /// <summary>True for locations that are shown by page (Pal Box, Dimensional Pal Storage).</summary>
    public static bool IsPaged(LocationKind kind) => kind is LocationKind.Palbox or LocationKind.DimensionalStorage;

    /// <summary>A location whose slot is a running index of the whole container (a save file): split into page and slot in the page.</summary>
    public static PalLocation FromRunningIndex(LocationKind kind, string? containerId, int? index, string? baseId) =>
        IsPaged(kind) && index is >= 0
            ? new PalLocation(kind, containerId, index % PageSize, baseId, index / PageSize)
            : new PalLocation(kind, containerId, index, baseId);
}

/// <summary>Which of several owned Pals of one kind a plan should use (ARCHITECTURE 7.3).</summary>
public static class OwnedPalOrder
{
    /// <summary>Best first: the higher IV total, a Pal whose IVs are unknown after those whose IVs are known, then the higher level, then the uid.</summary>
    public static IOrderedEnumerable<OwnedPal> BestFirst(this IEnumerable<OwnedPal> pals) =>
        pals.OrderByDescending(p => p.IvTotal.HasValue).ThenByDescending(p => p.IvTotal ?? 0).ThenByDescending(p => p.Level).ThenBy(p => p.Uid, StringComparer.Ordinal);
}

/// <summary>A Pal the user owns (from a save, the mod bridge or manual entry).</summary>
public sealed record OwnedPal(
    string Uid,
    string? InstanceId,
    string SpeciesId, Gender? Gender, int Level, int Rank,
    IReadOnlyList<string> Passives,
    Ivs? Ivs,
    IReadOnlyList<string> EquippedAttacks, IReadOnlyList<string> LearnedAttacks,
    string? Nickname, PalOwner? Owner, PalLocation Location,
    PalSourceKind Source, string SourceId, DateTimeOffset SeenAtUtc,
    bool IsEdited, bool IsExcluded,
    bool IsOnExpedition = false,
    bool IsAlpha = false)
{
    /// <summary>
    /// What the planner selections are keyed by: the in-game instance id when there is one (the same Pal read from a save and from the mod has
    /// two uids but one instance id), else the uid.
    /// </summary>
    [JsonIgnore]
    /// <summary>Sum of the three IVs; null when the source does not know them.</summary>
    public int? IvTotal => Ivs is { } i ? i.Hp + i.Attack + i.Defense : null;

    public string SelectionKey => string.IsNullOrEmpty(InstanceId) ? Uid : InstanceId;
}
