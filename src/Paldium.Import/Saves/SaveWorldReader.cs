using System.Diagnostics;
using System.Text.RegularExpressions;
using PalCalc.Model;
using PalCalc.SaveReader;
using PalCalc.SaveReader.SaveFile;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

using Paldium.Import.Migration;

namespace Paldium.Import.Saves;

/// <summary>Reads Palworld saves (ARCHITECTURE section 5, 6.1, 6.2). Read-only: the game files are never changed.</summary>
public interface ISaveWorldReader
{
    /// <summary>World folders under <c>%LOCALAPPDATA%\Pal\Saved\SaveGames\*</c>.</summary>
    IReadOnlyList<DiscoveredWorld> DiscoverLocalWorlds();

    /// <summary>Copies the world files to a temporary folder, then reads Level.sav, Players/*.sav and the _dps files.</summary>
    /// <exception cref="WorldUnavailableException">The folder has no Level.sav (the world is hosted by someone else).</exception>
    WorldSnapshot ReadWorld(string worldFolderPath);

    /// <summary>Reads <c>GlobalPalStorage.sav</c> of an account folder; an empty list when the file does not exist.</summary>
    IReadOnlyList<OwnedPal> ReadGlobalStorage(string accountFolderPath);
}

public sealed partial class SaveWorldReader : ISaveWorldReader
{
    public const string GlobalStorageFileName = "GlobalPalStorage.sav";

    [GeneratedRegex("^[0-9A-Fa-f]{32}$")]
    private static partial Regex WorldIdPattern();

    private readonly IGameData _data;
    private readonly string _workRoot;
    private readonly IReadOnlyList<string>? _accountFolders;
    private readonly Action<string, Exception?>? _log;
    private readonly TimeSpan _retryDelay;

    /// <param name="data">Game data used to recognize species and passives.</param>
    /// <param name="workRoot">Where temporary copies are made (default: <c>%LOCALAPPDATA%\Paldium\cache\read</c>).</param>
    /// <param name="accountFolders">Overrides the folders searched by <see cref="DiscoverLocalWorlds"/> (tests).</param>
    /// <param name="log">Receives progress and warnings.</param>
    /// <param name="retryDelay">Pause between the 3 copy attempts of a locked file (default 2 s).</param>
    public SaveWorldReader(
        IGameData data,
        string? workRoot = null,
        IReadOnlyList<string>? accountFolders = null,
        Action<string, Exception?>? log = null,
        TimeSpan? retryDelay = null)
    {
        _data = data;
        _workRoot = workRoot ?? Path.Combine(
            AppFolders.Local, "cache", "read");
        _accountFolders = accountFolders;
        _log = log;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>The account folders under <c>%LOCALAPPDATA%\Pal\Saved\SaveGames</c> (one per Steam user).</summary>
    public static IReadOnlyList<string> FindLocalAccountFolders() =>
        DirectSavesLocation.AllLocal.Select(l => l.FolderPath).Where(Directory.Exists).ToList();

    /// <summary>False when libooz cannot load (the Visual C++ x64 redistributable is missing).</summary>
    public static bool IsDecompressionAvailable => !LibOoz.IsMissingDependencies;

    // ---- discovery --------------------------------------------------------------------------------------------

    public IReadOnlyList<DiscoveredWorld> DiscoverLocalWorlds()
    {
        var accounts = _accountFolders ?? FindLocalAccountFolders();
        var result = new List<DiscoveredWorld>();
        foreach (var account in accounts)
        {
            if (!Directory.Exists(account)) continue;
            IEnumerable<string> folders;
            try { folders = Directory.EnumerateDirectories(account).Order(StringComparer.Ordinal).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var folder in folders)
            {
                var worldId = Path.GetFileName(folder);
                if (!WorldIdPattern().IsMatch(worldId)) continue;

                var hasLevel = File.Exists(Path.Combine(folder, "Level.sav"));
                result.Add(new DiscoveredWorld(
                    folder, worldId, hasLevel ? TryReadWorldName(folder) : null, hasLevel, LastWriteUtc(folder), account));
            }
        }

        return result;
    }

    private static DateTimeOffset LastWriteUtc(string worldFolder)
    {
        try
        {
            var times = Directory.EnumerateFiles(worldFolder, "*.sav").Select(f => File.GetLastWriteTimeUtc(f)).ToList();
            var players = Path.Combine(worldFolder, "Players");
            if (Directory.Exists(players))
                times.AddRange(Directory.EnumerateFiles(players, "*.sav").Select(f => File.GetLastWriteTimeUtc(f)));
            var latest = times.Count > 0 ? times.Max() : Directory.GetLastWriteTimeUtc(worldFolder);
            return new DateTimeOffset(latest, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private string? TryReadWorldName(string worldFolder)
    {
        var metaPath = Path.Combine(worldFolder, "LevelMeta.sav");
        if (!File.Exists(metaPath)) return null;
        using var temp = NewTempFolder();
        try
        {
            var copy = Path.Combine(temp.Path, "LevelMeta.sav");
            CopyFile(metaPath, copy);
            var meta = new LevelMetaSaveFile(new SingleFileSource(copy)).ReadGameOptions();
            return string.IsNullOrWhiteSpace(meta?.WorldName) ? null : meta.WorldName;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not read the world name of {worldFolder}.", ex);
            return null;
        }
    }

    // ---- world ------------------------------------------------------------------------------------------------

    public WorldSnapshot ReadWorld(string worldFolderPath)
    {
        if (!Directory.Exists(worldFolderPath))
            throw new DirectoryNotFoundException($"World folder not found: {worldFolderPath}");
        var levelPath = Path.Combine(worldFolderPath, "Level.sav");
        if (!File.Exists(levelPath)) throw WorldUnavailableException.HostedElsewhere(worldFolderPath);

        var stopwatch = Stopwatch.StartNew();
        var worldId = Path.GetFileName(Path.TrimEndingDirectorySeparator(worldFolderPath));
        var seenAt = new DateTimeOffset(File.GetLastWriteTimeUtc(levelPath), TimeSpan.Zero);

        using var temp = NewTempFolder();
        var copy = Path.Combine(temp.Path, worldId);
        CopyWorldFiles(worldFolderPath, copy);

        WorldSnapshot snapshot;
        using (var save = new StandardSaveGame(copy))
        {
            var worldName = TryReadMetaName(save);
            var levelData = save.Level.ReadCharacterData(PalDB.LoadEmbedded(), GameSettings.Defaults, save.Players, null);
            var alphaIds = SaveAlphaReader.ReadWorldAlphaIds(save, _log);
            snapshot = MapWorld(worldId, worldName, seenAt, levelData, alphaIds);
            snapshot = snapshot with { Captures = ReadCaptures(copy, snapshot.Players, save.Players) };
        }

        stopwatch.Stop();
        _log?.Invoke(
            $"Read world {worldId}: {snapshot.Pals.Count} Pals, {snapshot.Players.Count} players, {snapshot.Guilds.Count} guilds in {stopwatch.ElapsedMilliseconds} ms" +
            (snapshot.SkippedUnknownSpecies.Count > 0 ? $"; skipped unknown species: {string.Join(", ", snapshot.SkippedUnknownSpecies)}" : string.Empty) +
            (snapshot.DroppedUnknownPassives.Count > 0 ? $"; dropped unknown passives: {string.Join(", ", snapshot.DroppedUnknownPassives)}" : string.Empty),
            null);
        return snapshot with { ReadDuration = stopwatch.Elapsed };
    }

    /// <summary>The capture record of every player whose file is in the copied world (T121). A player without a file or with a broken one is left out.</summary>
    private IReadOnlyList<PlayerCapture> ReadCaptures(string worldCopy, IReadOnlyList<PlayerInfo> players, IReadOnlyList<PlayersSaveFile> savePlayers)
    {
        var result = new List<PlayerCapture>();
        Dictionary<string, string>? byInternalId = null;
        var now = DateTimeOffset.UtcNow;

        foreach (var player in players)
        {
            var normalizedId = CaptureProgressStore.NormalizeId(player.PlayerId);
            var file = Path.Combine(worldCopy, "Players", normalizedId.ToUpperInvariant() + ".sav");
            if (!File.Exists(file))
            {
                if (byInternalId == null)
                {
                    byInternalId = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var p in savePlayers)
                    {
                        var path = p.FilePaths.FirstOrDefault();
                        if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                        try
                        {
                            var meta = p.ReadPlayerContent();
                            if (!string.IsNullOrEmpty(meta?.PlayerId))
                                byInternalId.TryAdd(CaptureProgressStore.NormalizeId(meta.PlayerId), path);
                        }
                        catch (Exception ex)
                        {
                            _log?.Invoke($"Could not inspect player save {Path.GetFileName(path)} for PlayerUId.", ex);
                        }
                    }
                }
                if (!byInternalId.TryGetValue(normalizedId, out var matchedFile) || !File.Exists(matchedFile)) continue;
                file = matchedFile;
            }
            try
            {
                var record = PlayerCaptureReader.Read(file, _data, _log);
                if (record == null) continue;
                var readAt = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                if (readAt > now) readAt = now;
                result.Add(new PlayerCapture(player.PlayerId, record, readAt));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Could not read the capture record of player {player.PlayerId}.", ex);
            }
        }

        return result;
    }

    private string? TryReadMetaName(StandardSaveGame save)
    {
        try
        {
            if (!save.LevelMeta.Exists) return null;
            var name = save.LevelMeta.ReadGameOptions()?.WorldName;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            _log?.Invoke("Could not read the world name.", ex);
            return null;
        }
    }

    /// <summary>The player id of a Dimensional Pal Storage container id (<c>{PlayerId}_DPS</c>); null for any other id.</summary>
    public static string? StorageOwnerId(string? containerId) =>
        containerId != null && containerId.EndsWith("_DPS", StringComparison.OrdinalIgnoreCase) && containerId.Length > 4 ? containerId[..^4] : null;

    private WorldSnapshot MapWorld(string worldId, string? worldName, DateTimeOffset seenAt, LevelSaveData data, IReadOnlySet<string> alphaIds)
    {
        var mapping = new PalCalcMapping(_data);
        var players = data.Players.Select(p => new PlayerInfo(p.PlayerId, p.Name, p.Level)).ToList();
        var guilds = data.Guilds.Select(g => new GuildInfo(g.Id, g.Name, g.MemberIds ?? [])).ToList();

        var playerNames = data.Players.GroupBy(p => p.PlayerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var guildByPlayer = new Dictionary<string, GuildInstance>(StringComparer.OrdinalIgnoreCase);
        foreach (var guild in data.Guilds)
            foreach (var member in guild.MemberIds ?? [])
                guildByPlayer.TryAdd(member, guild);

        // Containers of bases and viewing cages know which base they belong to.
        var baseByContainer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in data.PalContainers)
        {
            switch (container)
            {
                case BasePalContainer b when b.BaseId != null: baseByContainer[b.Id] = b.BaseId; break;
                case ViewingCageContainer v when v.BaseId != null: baseByContainer[v.Id] = v.BaseId; break;
            }
        }

        var pals = new List<OwnedPal>();
        var expedition = new List<string>();
        foreach (var instance in data.Pals)
        {
            PalOwner? owner = null;
            if (!string.IsNullOrEmpty(instance.OwnerPlayerId))
            {
                playerNames.TryGetValue(instance.OwnerPlayerId, out var ownerName);
                guildByPlayer.TryGetValue(instance.OwnerPlayerId, out var guild);
                owner = new PalOwner(instance.OwnerPlayerId, ownerName, guild?.Id, guild?.Name);
            }

            baseByContainer.TryGetValue(instance.Location.ContainerId ?? string.Empty, out var baseId);
            var uid = $"save:{worldId}:{instance.InstanceId}";
            var pal = mapping.Map(instance, uid, PalSourceKind.Save, worldId, seenAt, owner, baseId);
            if (pal == null) continue;
            // The owner of a Dimensional Pal Storage is in its container id ("<player id>_DPS"), not in the Pal's own owner (the player who caught it).
            if (pal.Location.Kind == LocationKind.DimensionalStorage && StorageOwnerId(pal.Location.ContainerId) is { } storageOwner)
                pal = pal with { Location = pal.Location with { BaseName = playerNames.GetValueOrDefault(storageOwner) } };
            var isAlpha = !string.IsNullOrEmpty(pal.InstanceId) && alphaIds.Contains(pal.InstanceId);
            pals.Add(pal with { IsOnExpedition = instance.IsOnExpedition, IsAlpha = isAlpha });
            if (instance.IsOnExpedition) expedition.Add(uid);
        }

        // A save knows where each base is (its radius and name are not read by the save library).
        var bases = (data.Bases ?? []).Where(b => b.Position != null && !string.IsNullOrEmpty(b.Id)).Select(b =>
            new Paldium.Core.Owned.BaseSite(b.Id, b.Position.X, b.Position.Y)
            {
                Members = data.Guilds.FirstOrDefault(g => string.Equals(g.Id, b.OwnerGuildId, StringComparison.OrdinalIgnoreCase))?.MemberIds ?? [],
            }).ToList();

        return new WorldSnapshot(worldId, worldName, players, guilds, pals, DateTimeOffset.UtcNow)
        {
            Bases = bases,
            ExpeditionPalUids = expedition,
            SkippedUnknownSpecies = mapping.SkippedUnknownSpecies,
            DroppedUnknownPassives = mapping.DroppedUnknownPassives,
        };
    }

    // ---- global palbox ----------------------------------------------------------------------------------------

    public IReadOnlyList<OwnedPal> ReadGlobalStorage(string accountFolderPath)
    {
        var path = Path.Combine(accountFolderPath, GlobalStorageFileName);
        if (!File.Exists(path)) return [];

        var stopwatch = Stopwatch.StartNew();
        var seenAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        using var temp = NewTempFolder();
        var copy = Path.Combine(temp.Path, GlobalStorageFileName);
        CopyFile(path, copy);

        var gpsFile = new GlobalPalStorageSaveFile(new SingleFileSource(copy));
        var storage = gpsFile.ReadPals("GLOBAL_PAL_STORAGE");
        if (storage == null) return [];
        var alphaIds = SaveAlphaReader.ReadGlobalStorageAlphaIds(gpsFile, _log);

        // The Global Palbox belongs to the account, not to a world: no owner, so it counts in every ownership scope.
        var mapping = new PalCalcMapping(_data);
        var pals = new List<OwnedPal>();
        foreach (var instance in storage.Pals)
        {
            var pal = mapping.Map(instance, $"gps:{instance.InstanceId}", PalSourceKind.GlobalStorage,
                "gps:" + Path.GetFileName(Path.TrimEndingDirectorySeparator(accountFolderPath)), seenAt, owner: null, baseId: null);
            if (pal != null)
            {
                var isAlpha = !string.IsNullOrEmpty(pal.InstanceId) && alphaIds.Contains(pal.InstanceId);
                pals.Add(pal with { IsAlpha = isAlpha });
            }
        }

        _log?.Invoke($"Read Global Palbox: {pals.Count} Pals in {stopwatch.ElapsedMilliseconds} ms" +
                     (mapping.SkippedUnknownSpecies.Count > 0 ? $"; skipped unknown species: {string.Join(", ", mapping.SkippedUnknownSpecies)}" : string.Empty), null);
        return pals;
    }

    // ---- copying (K5: the game files are only ever read) ------------------------------------------------------

    private void CopyWorldFiles(string worldFolder, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(worldFolder, "*.sav"))
            CopyFile(file, Path.Combine(destination, Path.GetFileName(file)));

        var players = Path.Combine(worldFolder, "Players");
        if (!Directory.Exists(players)) return;
        var playersCopy = Path.Combine(destination, "Players");
        Directory.CreateDirectory(playersCopy);
        foreach (var file in Directory.EnumerateFiles(players, "*.sav"))
            CopyFile(file, Path.Combine(playersCopy, Path.GetFileName(file)));
    }

    // The game may be writing the file: open it with the most permissive sharing and retry a locked file 3 times.
    private void CopyFile(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }

                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
                return;
            }
            catch (IOException ex) when (attempt < 3)
            {
                _log?.Invoke($"Copy of {source} failed (attempt {attempt} of 3), retrying.", ex);
                Thread.Sleep(_retryDelay);
            }
        }
    }

    private TempFolder NewTempFolder()
    {
        var path = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempFolder(path, _log);
    }

    private sealed class TempFolder(string path, Action<string, Exception?>? log) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not delete temporary folder {Path}.", ex);
            }
        }
    }
}
