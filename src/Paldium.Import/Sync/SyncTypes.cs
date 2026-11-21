using Paldium.Core.Owned;

namespace Paldium.Import.Sync;

public enum SourceType
{
    /// <summary>A world folder (local, a synced folder or a dedicated server's folder); watched for changes.</summary>
    WorldFolder,

    /// <summary>A <c>GlobalPalStorage.sav</c> file (the account's Global Palbox); watched for changes.</summary>
    GlobalStorageFile,

    /// <summary>The <c>palbox.json</c> written by the PaldiumBridge mod (Path is the file); watched for changes.</summary>
    ModBridgeFile,

    /// <summary>A world folder extracted from a zip by <see cref="ZipImporter"/>; read on demand, not watched.</summary>
    ZipImport,
}

/// <summary>One configured Pal source (ARCHITECTURE section 5). <see cref="Path"/> is a folder, or the file for <see cref="SourceType.GlobalStorageFile"/>.</summary>
public sealed record SourceConfig(string Id, SourceType Type, string Path, bool Enabled);

/// <summary>Timing of the sync service (ARCHITECTURE 6.3). The defaults are the production values.</summary>
public sealed record SyncOptions
{
    /// <summary>Quiet period after the last change before a source is read again.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the files are checked for changes the watcher missed (cloud folders).</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>False disables FileSystemWatcher (tests of the polling path).</summary>
    public bool UseWatchers { get; init; } = true;
}

/// <summary>Keeps the owned-Pal repository up to date from the configured sources (ARCHITECTURE section 5, 6.3).</summary>
public interface ISyncService : IDisposable
{
    /// <summary>State of every configured source, in configuration order.</summary>
    IReadOnlyList<SourceStatus> Statuses { get; }

    /// <summary>Raised whenever a status changes; can come from any thread.</summary>
    event EventHandler? StatusesChanged;

    /// <summary>Reads one source (or every enabled one when null) now and completes when done.</summary>
    Task RefreshNowAsync(string? sourceId = null);

    /// <summary>Makes the given list the current configuration: new sources are read at once, removed ones are forgotten.</summary>
    void ApplyConfig(IReadOnlyList<SourceConfig> sources);
}
