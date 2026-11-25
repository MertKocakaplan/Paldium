using Paldium.Core.Domain;

namespace Paldium.Import.Saves;

/// <summary>A world folder found on this computer.</summary>
/// <param name="HasLevelSave">
/// False for worlds the user only joined (the folder holds just <c>LocalData.sav</c>): the Pal data lives on the host.
/// </param>
public sealed record DiscoveredWorld(
    string FolderPath,
    string WorldId,
    string? WorldName,
    bool HasLevelSave,
    DateTimeOffset LastWriteUtc,
    string AccountFolder);

public sealed record PlayerInfo(string PlayerId, string? Name, int Level);

public sealed record GuildInfo(string GuildId, string? Name, IReadOnlyList<string> MemberIds);

/// <summary>Everything read from one world. PalCalc types never appear here.</summary>
public sealed record WorldSnapshot(
    string WorldId,
    string? WorldName,
    IReadOnlyList<PlayerInfo> Players,
    IReadOnlyList<GuildInfo> Guilds,
    IReadOnlyList<OwnedPal> Pals,
    DateTimeOffset ReadAtUtc)
{
    /// <summary>Uids of Pals that were away on an expedition when the world was saved (they are included in <see cref="Pals"/>).</summary>
    public IReadOnlyList<string> ExpeditionPalUids { get; init; } = [];

    /// <summary>Internal names of species the game data does not know; those Pals were skipped.</summary>
    public IReadOnlyList<string> SkippedUnknownSpecies { get; init; } = [];

    /// <summary>Internal names of passives the game data does not know; they were left off the Pals.</summary>
    public IReadOnlyList<string> DroppedUnknownPassives { get; init; } = [];

    /// <summary>The capture records (T121) of the players whose <c>Players/&lt;id&gt;.sav</c> could be read.</summary>
    public IReadOnlyList<Paldium.Core.Owned.PlayerCapture> Captures { get; init; } = [];

    /// <summary>The bases of the world (T151) with the players of the owning guild.</summary>
    public IReadOnlyList<Paldium.Core.Owned.BaseSite> Bases { get; init; } = [];

    /// <summary>How long copying and reading took.</summary>
    public TimeSpan ReadDuration { get; init; }
}

/// <summary>Raised when a world cannot be read because its Pal data is not on this computer.</summary>
public sealed class WorldUnavailableException : Exception
{
    /// <summary>Localization key for the UI.</summary>
    public const string HostedElsewhereKey = "Import.Error.HostedElsewhere";

    public WorldUnavailableException(string key, string message) : base(message)
    {
        Key = key;
    }

    public string Key { get; }

    public static WorldUnavailableException HostedElsewhere(string folder) =>
        new(HostedElsewhereKey,
            $"This world is hosted by another player; the Pal data is not on this computer ({folder}).");
}
