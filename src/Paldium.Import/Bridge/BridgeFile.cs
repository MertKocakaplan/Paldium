using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paldium.Import.Bridge;

// JSON written by the PaldiumBridge mod (ARCHITECTURE 6.5). Every field except schemaVersion and the Pal's characterId may be missing.

/// <param name="BoxOwnerUid">Owner of the storage box (a Dimensional Pal Storage exists once per player; a shared one can belong to the host).</param>
/// <param name="BaseId">Id of the base for <c>Base</c> Pals (the Pals that work there).</param>
/// <param name="BaseName">The base's name in the game (<c>BaseCampName</c>), when the mod could read it.</param>
/// <param name="BoxId">Key of the box when the owner is not a uid: <c>name:&lt;player&gt;</c> or <c>unknown-&lt;n&gt;</c> (every opening of a box with an unknown owner is its own box).</param>
public sealed record BridgeLocation(string Kind, int? Page = null, int? Slot = null, string? BoxOwnerUid = null, string? BoxId = null, string? BaseId = null, string? BaseName = null);

/// <summary>A player the game state knows (the mod's <c>players</c> array); the sources panel offers these names for a storage of unknown owner.</summary>
public sealed record BridgePlayer(string Uid, string? Name = null);

/// <summary>What the mod has read of one box so far (a page is only sent to the client while it is shown).</summary>
public sealed record BridgeCoverage
{
    public string Kind { get; init; } = "";
    public string? BoxOwnerUid { get; init; }

    /// <summary>Key of the box (the uid, <c>name:&lt;player&gt;</c> or <c>unknown-&lt;n&gt;</c>).</summary>
    public string? BoxId { get; init; }

    /// <summary>The owner's name read from the storage menu title ("&lt;Player&gt;'s Dimensional Pal Storage"), when known.</summary>
    public string? OwnerName { get; init; }

    /// <summary>How the box owner was found: <c>storage</c>, <c>locker</c>, <c>structure</c>, <c>title</c> or <c>user</c> (chosen in the app; absent: unknown).</summary>
    public string? OwnerSource { get; init; }
    public int? PageCount { get; init; }

    /// <summary>Pals on the stored pages of the box (set by <see cref="BridgePageStore"/>, not by the mod).</summary>
    public int? PalCount { get; init; }
    public IReadOnlyList<int> PagesRead { get; init; } = [];
    public DateTimeOffset? LastReadUtc { get; init; }

    /// <summary>The oldest read among the stored pages (set by <see cref="BridgePageStore"/>, not by the mod).</summary>
    public DateTimeOffset? OldestReadUtc { get; init; }

    // A base (T151): where it is (world units), its radius and its worker slots; absent in files of older mod versions.
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public double? Range { get; init; }
    public int? Capacity { get; init; }

    /// <summary>Pages below the highest page read that were not read. Empty pages are not known, so nothing above the highest page is listed.</summary>
    public IReadOnlyList<int> MissingPages
    {
        get
        {
            if (PagesRead.Count == 0) return [];
            var read = PagesRead.ToHashSet();
            return Enumerable.Range(0, PagesRead.Max()).Where(p => !read.Contains(p)).ToList();
        }
    }
}

public sealed record BridgeTalents(int? Hp = null, int? Shot = null, int? Defense = null);

public sealed record BridgePal
{
    public string? InstanceId { get; init; }
    public required string CharacterId { get; init; }
    public string? Gender { get; init; }
    public int? Level { get; init; }
    public int? Rank { get; init; }
    public IReadOnlyList<string> Passives { get; init; } = [];
    public BridgeTalents? Talents { get; init; }
    public IReadOnlyList<string> EquippedAttacks { get; init; } = [];
    public IReadOnlyList<string> LearnedAttacks { get; init; } = [];
    public string? Nickname { get; init; }

    /// <summary>Player uid of the owner when the game gives one (a shared Dimensional Pal Storage can hold other players' Pals).</summary>
    public string? OwnerUid { get; init; }
    public BridgeLocation? Location { get; init; }

    /// <summary>When the page this Pal was read from was read (set by <see cref="BridgePageStore"/>; the mod does not write it).</summary>
    public DateTimeOffset? ReadAtUtc { get; init; }
}

/// <summary>The own player's capture progress as the mod reads it (T121): species name to bonus catches (0 to 5) and to all catches.</summary>
public sealed record BridgeCapture(IReadOnlyDictionary<string, int>? Bonus = null, IReadOnlyDictionary<string, int>? Count = null);

/// <summary>The effigies of one type as the mod reads them (T133): unspent (<c>have</c>) and collected from the world (<c>collected</c>); either may be absent.</summary>
public sealed record BridgeRelic(int? Have = null, int? Collected = null);

/// <summary>An active quest as the mod reads it (T150): the quest id and the step it is at, counted from 0.</summary>
public sealed record BridgeQuest(string? Id = null, int? Block = null);

/// <summary>
/// What the own player has taken, opened and done (T144, T148–T150): level object ids for effigies, ancient towers and fast travel points, journal
/// row names, tower boss keys, quests and the defeated predators. A field the mod could not read is absent.
/// </summary>
public sealed record BridgeObtained
{
    public IReadOnlyList<string>? Relic { get; init; }
    public IReadOnlyList<string>? Pickup { get; init; }
    public IReadOnlyList<string>? FastTravel { get; init; }
    public IReadOnlyList<string>? Note { get; init; }
    public IReadOnlyList<string>? TowerBoss { get; init; }
    public IReadOnlyList<BridgeQuest>? QuestsActive { get; init; }
    public IReadOnlyList<string>? QuestsDone { get; init; }
    public int? PredatorDefeats { get; init; }
}

public sealed record BridgeFile
{
    public const int SupportedSchemaVersion = 1;

    public int SchemaVersion { get; init; }
    public DateTimeOffset? ExportedAtUtc { get; init; }
    public string? GameBuild { get; init; }
    public string? PlayerName { get; init; }
    public string? PlayerUid { get; init; }

    /// <summary>The world the file was read in (from the game state; absent when the mod could not find it: the player uid is used then).</summary>
    public string? WorldId { get; init; }
    public IReadOnlyList<BridgePal> Pals { get; init; } = [];

    /// <summary>The players of the game (absent in files of older mod versions).</summary>
    public IReadOnlyList<BridgePlayer> Players { get; init; } = [];

    /// <summary>Capture progress of the own player (absent when the mod could not read it).</summary>
    public BridgeCapture? Capture { get; init; }

    /// <summary>The effigies of the own player by <c>EPalRelicType</c> name (T133; absent when the mod could not read them).</summary>
    public IReadOnlyDictionary<string, BridgeRelic>? Relics { get; init; }

    /// <summary>The world progress of the own player (T144; absent in files of older mod versions).</summary>
    public BridgeObtained? Obtained { get; init; }

    /// <summary>Per-box report of the pages read (absent in files of older mod versions).</summary>
    public IReadOnlyList<BridgeCoverage> Coverage { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Parses the file; throws <see cref="InvalidDataException"/> for malformed JSON or an unsupported schema version.</summary>
    public static BridgeFile Parse(string json)
    {
        BridgeFile? file;
        try
        {
            file = JsonSerializer.Deserialize<BridgeFile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The bridge file is not valid: " + ex.Message, ex);
        }

        if (file == null) throw new InvalidDataException("The bridge file is empty.");
        if (file.SchemaVersion != SupportedSchemaVersion)
            throw new InvalidDataException($"Unsupported bridge schema version {file.SchemaVersion}.");
        return file;
    }
}

internal static class BridgeFileIo
{
    /// <summary>Opens the bridge file with read/write/delete sharing so Lua atomic renames never lose updates on Windows.</summary>
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
