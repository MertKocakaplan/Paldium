using System.IO;
using Paldium.Core.Domain;
using Paldium.Import.Bridge;
using Paldium.Import.Saves;
using Paldium.Import.Sync;

namespace Paldium.App.Services;

/// <summary>Raised when something the user picked cannot be used as a source; <see cref="Key"/> is a localization key.</summary>
public sealed class SourceException : Exception
{
    public const string NoWorld = "Sources.NoLevel";
    public const string ZipFailed = "Sources.ZipFailed";
    public const string NoGlobal = "Sources.NoGlobalFound";

    public SourceException(string key, string detail) : base(detail)
    {
        Key = key;
    }

    public string Key { get; }
}

/// <summary>
/// Manages the configured Pal sources: persists them in the settings, hands them to the sync service and creates
/// them from what the user picks (ARCHITECTURE 6.2, T12).
/// </summary>
public sealed class SourcesService
{
    public const string GlobalStorageFileName = "GlobalPalStorage.sav";

    private readonly SettingsStore _settings;
    private readonly ISyncService _sync;
    private readonly ZipImporter _zip;
    private readonly Func<IReadOnlyList<string>> _accountFolders;
    private readonly Action<string, Exception?>? _log;
    private readonly string _bridgeFile;
    private readonly object _gate = new();

    public SourcesService(
        SettingsStore settings,
        ISyncService sync,
        ZipImporter zip,
        Func<IReadOnlyList<string>>? accountFolders = null,
        Action<string, Exception?>? log = null,
        string? bridgeFile = null)
    {
        _bridgeFile = bridgeFile ?? ModBridgeReader.DefaultFile;
        _settings = settings;
        _sync = sync;
        _zip = zip;
        _accountFolders = accountFolders ?? SaveWorldReader.FindLocalAccountFolders;
        _log = log;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SourceConfig> Sources => _settings.Current.Sources;

    /// <summary>
    /// Applies the saved sources. The Global Palbox is not added automatically (T37: it is only a genetic copy, not an owned Pal); an
    /// automatic one from an older version is removed once.
    /// </summary>
    public void Start()
    {
        if (!_settings.Current.GlobalPalboxMigrated) MigrateGlobalPalbox();
        OfferBridgeOnce();
        _sync.ApplyConfig(Sources);
    }

    // One time: drop the Global Palbox sources of the local accounts (the old first-run setup added them) and take GlobalStorage
    // out of the scope. A Global Palbox the user adds by hand later is kept.
    private void MigrateGlobalPalbox()
    {
        var local = new List<string>();
        try
        {
            local.AddRange(_accountFolders().Select(folder => Normalize(Path.Combine(folder, GlobalStorageFileName))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("Looking for local Global Palbox files failed.", ex);
        }

        _settings.Update(s => s with
        {
            GlobalPalboxMigrated = true,
            Sources = s.Sources.Where(src => !(src.Type == SourceType.GlobalStorageFile &&
                local.Contains(Normalize(src.Path), StringComparer.OrdinalIgnoreCase))).ToList(),
            Scope = s.Scope with { Locations = s.Scope.Locations.Where(l => l != LocationKind.GlobalStorage).ToList() },
        });
    }
    /// <summary>The file the PaldiumBridge mod writes (it may not exist yet).</summary>
    public string BridgeFile => _bridgeFile;

    public bool BridgeFileExists => File.Exists(_bridgeFile);

    /// <summary>True when the bridge file exists and is not a source (yet or any more): the panel offers to add it.</summary>
    public bool BridgeSuggested => BridgeFileExists && !IsKnown(SourceType.ModBridgeFile, _bridgeFile);

    public SourceConfig AddBridgeFile() => Add(SourceType.ModBridgeFile, _bridgeFile);

    // The first time the file is seen it becomes a source by itself; after the user removed it, it only stays a suggestion.
    private void OfferBridgeOnce()
    {
        if (_settings.Current.BridgeOffered || !BridgeFileExists) return;
        _settings.Update(s => s with { BridgeOffered = true });
        if (!IsKnown(SourceType.ModBridgeFile, _bridgeFile))
            _settings.Update(s => s with { Sources = s.Sources.Append(new SourceConfig(Guid.NewGuid().ToString("N"), SourceType.ModBridgeFile, Path.GetFullPath(_bridgeFile), true)).ToList() });
    }

    /// <summary>Global Palbox files of the local accounts that are not a source yet.</summary>
    public IReadOnlyList<string> FindLocalGlobalPalboxFiles() =>
        _accountFolders()
            .Select(folder => Path.Combine(folder, GlobalStorageFileName))
            .Where(File.Exists)
            .Where(file => !IsKnown(SourceType.GlobalStorageFile, file))
            .ToList();

    /// <summary>Adds every local Global Palbox that is not a source yet; returns what was added.</summary>
    public IReadOnlyList<SourceConfig> AddLocalGlobalPalboxes() =>
        FindLocalGlobalPalboxFiles().Select(file => Add(SourceType.GlobalStorageFile, file)).ToList();

    public SourceConfig AddGlobalStorageFile(string file)
    {
        if (!File.Exists(file)) throw new SourceException(SourceException.NoGlobal, file);
        return Add(SourceType.GlobalStorageFile, file);
    }

    /// <summary>Adds a folder as a watched world source; the folder itself or a parent of the world folder works.</summary>
    public SourceConfig AddWorldFolder(string folder)
    {
        var world = Directory.Exists(folder) ? ZipImporter.FindWorldFolder(folder) : null;
        if (world == null) throw new SourceException(SourceException.NoWorld, folder);
        return Add(SourceType.WorldFolder, world);
    }

    public SourceConfig AddDiscoveredWorld(DiscoveredWorld world) => Add(SourceType.WorldFolder, world.FolderPath);

    /// <summary>Extracts a zip and adds the world inside it (read on demand, not watched).</summary>
    public SourceConfig AddZip(string zipPath)
    {
        ZipImportResult result;
        try
        {
            result = _zip.Import(zipPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new SourceException(SourceException.ZipFailed, ex.Message);
        }

        return Add(SourceType.ZipImport, result.WorldFolder);
    }

    public bool IsKnown(SourceType type, string path)
    {
        var normalized = Normalize(path);
        return Sources.Any(s => s.Type == type && string.Equals(Normalize(s.Path), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void SetEnabled(string id, bool enabled) =>
        Mutate(sources => sources.Select(s => s.Id == id ? s with { Enabled = enabled } : s).ToList());

    public void Remove(string id)
    {
        var source = Sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        Mutate(sources => sources.Where(s => !string.Equals(s.Id, id, StringComparison.Ordinal)).ToList());

        // The extracted copy of a zip is ours to clean up.
        if (source is { Type: SourceType.ZipImport }) _zip.TryRemove(source.Path);
    }

    public Task RefreshAsync(string? id = null) => _sync.RefreshNowAsync(id);

    private SourceConfig Add(SourceType type, string path)
    {
        lock (_gate)
        {
            var existing = Sources.FirstOrDefault(s =>
                s.Type == type && string.Equals(Normalize(s.Path), Normalize(path), StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var config = new SourceConfig(Guid.NewGuid().ToString("N"), type, Path.GetFullPath(path), true);
            Mutate(sources => sources.Append(config).ToList());
            return config;
        }
    }

    private void Mutate(Func<IReadOnlyList<SourceConfig>, IReadOnlyList<SourceConfig>> change)
    {
        _settings.Update(s => s with { Sources = change(s.Sources) });
        _sync.ApplyConfig(Sources);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
