using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Import.Migration;
using Paldium.Import.Saves;
using DomainLocation = Paldium.Core.Domain.PalLocation;

namespace Paldium.Import.Bridge;

/// <summary>Short description of a bridge file for the sources panel (ARCHITECTURE 6.5).</summary>
/// <summary>A record of the file that is not imported: a captured human (an NPC, never part of breeding) or a species the data bundle does not know.</summary>
public sealed record BridgeSkipped(string Id, bool IsHuman);

/// <param name="PalCount">Pals in the file.</param>
/// <param name="Coverage">What the mod has read per box (empty for files of older mod versions).</param>
/// <param name="Skipped">Records that are not imported (they are not part of the source's Pal count).</param>
public sealed record BridgeSummary(
    DateTimeOffset? ExportedAtUtc, int PalCount, int PartyCount, IReadOnlyList<BridgeCoverage> Coverage, IReadOnlyList<BridgeSkipped> Skipped, string? WorldId = null)
{
    /// <summary>Pals that end up in the list (the same number as the source row shows).</summary>
    public int ImportedCount => PalCount - Skipped.Count;

    /// <summary>Pages read of one kind of box, summed over its boxes; empty pages count (they were read).</summary>
    public int PagesRead(string kind) => Coverage.Where(c => c.Kind == kind).Sum(c => c.PagesRead.Count);
}

public interface IModBridgeReader
{
    /// <summary>Reads the bridge file written by the PaldiumBridge mod. Throws <see cref="InvalidDataException"/> for a broken file.</summary>
    IReadOnlyList<OwnedPal> Read(string file, string sourceId);

    /// <summary>The capture record the mod wrote for its own player (T121); empty when the file has none.</summary>
    IReadOnlyList<Paldium.Core.Owned.PlayerCapture> ReadCaptures(string file) => [];

    /// <summary>Reads both owned Pals and capture/relic records from a single read of the bridge file.</summary>
    (IReadOnlyList<OwnedPal> Pals, IReadOnlyList<Paldium.Core.Owned.PlayerCapture> Captures) ReadAll(string file, string sourceId) =>
        (Read(file, sourceId), ReadCaptures(file));
}

/// <summary>Converts the mod's JSON file to owned Pals. Read-only; a half written or broken file is an error (the previous data stays).</summary>
public sealed class ModBridgeReader : IModBridgeReader
{
    public const string FileName = "palbox.json";

    private readonly IGameData _data;
    private readonly Action<string, Exception?>? _log;
    private readonly BridgePageStore? _pages;

    /// <param name="pages">Keeps the pages of earlier game sessions (T41); without it the file is read as it is.</param>
    public ModBridgeReader(IGameData data, Action<string, Exception?>? log = null, BridgePageStore? pages = null)
    {
        _data = data;
        _log = log;
        _pages = pages;
    }

    /// <summary>Default place of the file: <c>%LOCALAPPDATA%\Paldium\bridge\palbox.json</c>.</summary>
    public static string DefaultFile => Path.Combine(AppFolders.Local, "bridge", FileName);

    /// <summary>The place the mod of the former name writes to (T129): read until the new mod is installed.</summary>
    public static string LegacyFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolders.LegacyName, "bridge", FileName);

    /// <summary>
    /// The file to read for a configured bridge path: for the default place, the old mod's file when it is newer (or the only one), so the Pals keep coming in
    /// until the new mod is installed. Any other path is used as it is.
    /// </summary>
    public static string EffectiveFile(string file) => EffectiveFile(file, DefaultFile, LegacyFile);

    public static string EffectiveFile(string file, string defaultFile, string legacy)
    {
        if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(defaultFile), StringComparison.OrdinalIgnoreCase)) return file;
        if (!File.Exists(legacy)) return file;
        if (!File.Exists(file)) return legacy;
        return File.GetLastWriteTimeUtc(legacy) > File.GetLastWriteTimeUtc(file) ? legacy : file;
    }

    // Species the game has but PalCalc lists as humans (captured NPCs): never part of breeding, so not imported.
    private static readonly string[] HumanPrefixes =
    [
        "Hunter_", "Police_", "Guard_", "Robbery_", "Believer_", "FireCult_", "Visitor_", "Arena_", "Male_", "Female_", "Scientist", "SalesPerson",
        "PalDealer", "WeaponsDealer", "VisitingMerchant", "Mobu", "Quest", "Viking", "Ninja", "Yamishima", "Inspector", "PalTrader", "GoodwillAmbassador",
        "SecurityDrone", "TestNPC", "TestAssassin", "Reward", "Emote", "NPC_",
    ];

    public static bool IsHumanId(string id) => HumanPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <param name="data">When given, records the data bundle does not know are listed as skipped.</param>
    public static BridgeSummary? Summarize(string file, IGameData? data = null)
    {
        try
        {
            var bridge = BridgeFile.Parse(BridgeFileIo.ReadAllText(file));
            var skipped = data == null
                ? []
                : bridge.Pals.Where(p => !data.TryNormalizePalId(p.CharacterId, out _)).Select(p => new BridgeSkipped(p.CharacterId, IsHumanId(p.CharacterId))).ToList();
            return new BridgeSummary(bridge.ExportedAtUtc, bridge.Pals.Count, bridge.Pals.Count(p => p.Location?.Kind == "Party"), bridge.Coverage, skipped, BridgePageStore.WorldOf(bridge));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<OwnedPal> Read(string file, string sourceId)
    {
        // A missing file is an error, not "no Pals": the mod deletes the old file for a moment while it renames the new one,
        // and the previous data must survive that.
        var bridge = BridgeFile.Parse(BridgeFileIo.ReadAllText(file));
        return ReadCore(bridge, file, sourceId);
    }

    public (IReadOnlyList<OwnedPal> Pals, IReadOnlyList<Paldium.Core.Owned.PlayerCapture> Captures) ReadAll(string file, string sourceId)
    {
        var bridge = BridgeFile.Parse(BridgeFileIo.ReadAllText(file));
        return (ReadCore(bridge, file, sourceId), ReadCapturesCore(bridge, file));
    }

    private IReadOnlyList<OwnedPal> ReadCore(BridgeFile bridge, string file, string sourceId)
    {
        // The Pals of every stored page of this world: pages of earlier sessions stay until they are read again.
        var boxNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_pages != null)
        {
            var merged = _pages.Merge(sourceId, bridge);
            bridge = bridge with { Pals = merged.Pals };
            // The name of the owner of every Dimensional Pal Storage: read from the game, or chosen by the user, or the name of the player with that uid.
            foreach (var coverage in merged.Coverage.Where(c => c.Kind == "DimensionalStorage" && !string.IsNullOrEmpty(c.BoxId)))
            {
                var name = !string.IsNullOrEmpty(coverage.OwnerName) ? coverage.OwnerName
                    : merged.Players?.FirstOrDefault(p => string.Equals(p.Uid, coverage.BoxOwnerUid, StringComparison.OrdinalIgnoreCase))?.Name;
                if (!string.IsNullOrEmpty(name)) boxNames[coverage.BoxId!] = name;
            }
        }
        var utcNow = DateTimeOffset.UtcNow;
        var seen = bridge.ExportedAtUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        if (seen > utcNow) seen = utcNow;
        var owner = string.IsNullOrEmpty(bridge.PlayerUid) ? null : new PalOwner(bridge.PlayerUid, bridge.PlayerName, null, null);
        var result = new List<OwnedPal>();
        var uids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pal in bridge.Pals)
        {
            // The game's row names ("BOSS_Anubis") map to the species.
            if (!_data.TryNormalizePalId(pal.CharacterId, out var species))
            {
                _log?.Invoke(IsHumanId(pal.CharacterId)
                    ? $"Bridge: human '{pal.CharacterId}' is not a Pal and was not imported."
                    : $"Bridge: unknown species '{pal.CharacterId}' skipped.", null);
                continue;
            }

            var kind = Enum.TryParse<LocationKind>(pal.Location?.Kind, out var parsedKind) ? parsedKind : LocationKind.Unknown;
            var uid = !string.IsNullOrEmpty(pal.InstanceId)
                ? "mod:" + pal.InstanceId
                : $"mod:{kind}:{(BoxOf(pal.Location) is { } box ? box + ":" : "")}{pal.Location?.Page ?? 0}:{pal.Location?.Slot ?? 0}";
            if (!uids.Add(uid)) continue;

            var palSeen = pal.ReadAtUtc ?? seen;
            if (palSeen > utcNow) palSeen = utcNow;

            result.Add(new OwnedPal(
                Uid: uid,
                InstanceId: string.IsNullOrEmpty(pal.InstanceId) ? null : pal.InstanceId,
                SpeciesId: species,
                Gender: pal.Gender switch { "Male" => Gender.Male, "Female" => Gender.Female, _ => null },
                Level: Math.Max(1, pal.Level ?? 1),
                Rank: Math.Clamp(pal.Rank ?? 1, 1, 5),
                Passives: KnownPassives(pal.Passives),
                Ivs: pal.Talents is { Hp: { } hp, Shot: { } shot, Defense: { } defense } ? new Ivs(Clamp(hp), Clamp(shot), Clamp(defense)) : null,
                EquippedAttacks: KnownAttacks(pal.EquippedAttacks),
                LearnedAttacks: KnownAttacks(pal.LearnedAttacks),
                Nickname: string.IsNullOrWhiteSpace(pal.Nickname) ? null : pal.Nickname,
                Owner: NormalizeOwner(pal.OwnerUid) is not { } palOwner || palOwner == bridge.PlayerUid ? owner : new PalOwner(palOwner, null, null, null),
                Location: new DomainLocation(kind, BoxOf(pal.Location), pal.Location?.Slot, string.IsNullOrEmpty(pal.Location?.BaseId) ? null : pal.Location.BaseId,
                    DomainLocation.IsPaged(kind) ? pal.Location?.Page : null,
                    kind == LocationKind.Base && !string.IsNullOrWhiteSpace(pal.Location?.BaseName) && !IsPlaceholderBaseName(pal.Location.BaseName) ? pal.Location.BaseName
                        : kind == LocationKind.DimensionalStorage && BoxOf(pal.Location) is { } storageBox && boxNames.TryGetValue(storageBox, out var ownerName) ? ownerName : null),
                Source: PalSourceKind.ModBridge,
                SourceId: sourceId,
                SeenAtUtc: palSeen,
                IsEdited: false,
                IsExcluded: false));
        }

        return result;
    }

    public IReadOnlyList<Paldium.Core.Owned.PlayerCapture> ReadCaptures(string file) =>
        ReadCapturesCore(BridgeFile.Parse(BridgeFileIo.ReadAllText(file)), file);

    private IReadOnlyList<Paldium.Core.Owned.PlayerCapture> ReadCapturesCore(BridgeFile bridge, string file)
    {
        if ((bridge.Capture == null && bridge.Relics == null) || NormalizeOwner(bridge.PlayerUid) is not { } uid) return [];
        Dictionary<string, Paldium.Core.Owned.RelicCount>? relics = null;
        if (bridge.Relics != null)
        {
            relics = new Dictionary<string, Paldium.Core.Owned.RelicCount>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rawKey, val) in bridge.Relics)
            {
                if (val == null || string.IsNullOrWhiteSpace(rawKey)) continue;
                var name = PlayerCaptureReader.RelicTypeName(rawKey);
                if (string.IsNullOrWhiteSpace(name)) continue;
                relics[name] = new Paldium.Core.Owned.RelicCount(val.Have, val.Collected);
            }
        }
        var record = PlayerCaptureReader.Normalize(
            new Paldium.Core.Owned.CaptureRecord(bridge.Capture?.Bonus ?? new Dictionary<string, int>(), bridge.Capture?.Count ?? new Dictionary<string, int>()) { Relics = relics }, _data, _log);
        if (bridge.Capture == null && (relics == null || relics.Count == 0)) return [];
        var utcNow = DateTimeOffset.UtcNow;
        var seen = bridge.ExportedAtUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        if (seen > utcNow) seen = utcNow;
        return [new Paldium.Core.Owned.PlayerCapture(uid, record, seen)];
    }

    /// <summary>The box a bridge location is in: the storage box id or owner (several Dimensional Pal Storages), or the base id; null for the Pal Box and the party.</summary>
    public static string? BoxOf(BridgeLocation? location)
    {
        if (location == null) return null;
        return !string.IsNullOrEmpty(location.BoxId) ? location.BoxId : NormalizeOwner(location.BoxOwnerUid) ?? (string.IsNullOrEmpty(location.BaseId) ? null : location.BaseId);
    }

    /// <summary>
    /// The game's template name of a base the player never named (Japanese: "(temporary)" or "template name"). The player does not see it in the game, so it
    /// is not shown; older bridge files and older mod versions still carry it. The mod filters the same names.
    /// </summary>
    public static bool IsPlaceholderBaseName(string? name) =>
        !string.IsNullOrEmpty(name) && (name.Contains("(\u4eee)", StringComparison.Ordinal) || name.Contains("\uff08\u4eee\uff09", StringComparison.Ordinal) ||
                                        name.Contains("\u30c6\u30f3\u30d7\u30ec\u30fc\u30c8", StringComparison.Ordinal));

    /// <summary>An all-zero uid means "unknown" (the client does not always know a box's owner); it is never used as an owner.</summary>
    public static string? NormalizeOwner(string? uid) => string.IsNullOrEmpty(uid) || uid.All(c => c == '0') ? null : uid;

    private static int Clamp(int iv) => Math.Clamp(iv, 0, 100);

    private IReadOnlyList<string> KnownPassives(IReadOnlyList<string> raw)
    {
        var result = new List<string>();
        foreach (var name in raw)
        {
            var known = _data.Passives.FirstOrDefault(p => string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase));
            if (known == null) _log?.Invoke($"Bridge: unknown passive '{name}' dropped.", null);
            else if (!result.Contains(known.Id, StringComparer.Ordinal)) result.Add(known.Id);
        }

        return result.Take(4).ToList();
    }

    // The mod writes enum names such as "AirBlade"; an "EPalWazaID::" prefix is tolerated. Unknown ids are dropped like unknown passives.
    private IReadOnlyList<string> KnownAttacks(IReadOnlyList<string> raw)
    {
        var result = new List<string>();
        foreach (var name in raw)
        {
            var id = name.Contains("::", StringComparison.Ordinal) ? name[(name.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : name;
            var known = _data.Actives.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (known == null) _log?.Invoke($"Bridge: unknown attack '{name}' dropped.", null);
            else if (!result.Contains(known.Id, StringComparer.Ordinal)) result.Add(known.Id);
        }

        return result;
    }
}
