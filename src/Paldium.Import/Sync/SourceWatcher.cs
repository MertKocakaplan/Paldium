using System.Security.Cryptography;
using System.Text;

namespace Paldium.Import.Sync;

/// <summary>
/// Watches one source for changes (ARCHITECTURE 6.3): a FileSystemWatcher plus a polling check of the file
/// signatures (cloud-synced folders can miss events). Changes are debounced; <paramref name="onChanged"/> runs once
/// the files have been quiet for <see cref="SyncOptions.Debounce"/>.
/// </summary>
internal sealed class SourceWatcher : IDisposable
{
    private readonly SourceConfig _config;
    private readonly SyncOptions _options;
    private readonly Action _onChanged;
    private readonly object _gate = new();
    private readonly Timer _debounce;
    private readonly Timer? _poll;
    private FileSystemWatcher? _watcher;
    private string? _lastReadSignature;
    private bool _disposed;

    public SourceWatcher(SourceConfig config, SyncOptions options, Action onChanged)
    {
        _config = config;
        _options = options;
        _onChanged = onChanged;
        _debounce = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);

        if (options.UseWatchers) TryStartWatcher();
        _poll = new Timer(_ => Poll(), null, options.PollInterval, options.PollInterval);
    }

    /// <summary>Remembers the signature of the files that were just read, so polling only reacts to newer changes.</summary>
    public void SetReadSignature(string signature)
    {
        lock (_gate) _lastReadSignature = signature;
    }

    /// <summary>A fingerprint (name, size, write time) of every file the source consists of.</summary>
    public static string ComputeSignature(SourceConfig config)
    {
        var builder = new StringBuilder();
        try
        {
            foreach (var file in RelevantFiles(config).Order(StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                builder.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            builder.Append("unreadable");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Cancels a change that is waiting for its debounce period.</summary>
    public void CancelPending() => _debounce.Change(Timeout.Infinite, Timeout.Infinite);

    private static IEnumerable<string> RelevantFiles(SourceConfig config)
    {
        if (config.Type is SourceType.GlobalStorageFile or SourceType.ModBridgeFile)
        {
            var file = config.Type == SourceType.ModBridgeFile ? Bridge.ModBridgeReader.EffectiveFile(config.Path) : config.Path;
            if (File.Exists(file)) yield return file;
            yield break;
        }

        if (!Directory.Exists(config.Path)) yield break;
        foreach (var file in Directory.EnumerateFiles(config.Path, "*.sav")) yield return file;

        var players = Path.Combine(config.Path, "Players");
        if (!Directory.Exists(players)) yield break;
        foreach (var file in Directory.EnumerateFiles(players, "*.sav")) yield return file;
    }

    private void TryStartWatcher()
    {
        try
        {
            if (_config.Type is SourceType.GlobalStorageFile or SourceType.ModBridgeFile)
            {
                var directory = Path.GetDirectoryName(_config.Path);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
                _watcher = new FileSystemWatcher(directory, Path.GetFileName(_config.Path)) { IncludeSubdirectories = false };
            }
            else
            {
                if (!Directory.Exists(_config.Path)) return;
                _watcher = new FileSystemWatcher(_config.Path, "*.sav") { IncludeSubdirectories = true };
            }

            _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime;
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Polling still covers the source (for example a network share without change notifications).
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Older copies of the world live in a "backup" folder; they are not part of the source.
        var relative = Path.GetRelativePath(_config.Path, e.FullPath);
        if (relative.StartsWith("backup" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        Schedule();
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Poll()
    {
        string? last;
        lock (_gate)
        {
            if (_disposed) return;
            last = _lastReadSignature;
        }

        if (last != null && ComputeSignature(_config) != last) Schedule();
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        try
        {
            _onChanged();
        }
        catch
        {
            // The owner logs read failures; a callback error must never kill the timer thread.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _watcher?.Dispose();
        _poll?.Dispose();
        _debounce.Dispose();
    }
}
