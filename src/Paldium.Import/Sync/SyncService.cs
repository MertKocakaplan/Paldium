using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Import.Saves;

namespace Paldium.Import.Sync;

/// <summary>
/// Watches the configured sources, reads them in the background and publishes the Pals to the repository
/// (ARCHITECTURE 6.3). A failed read keeps the previous data and shows the error in the source status.
/// </summary>
public sealed class SyncService : ISyncService
{
    private sealed class Runtime(SourceConfig config)
    {
        public SourceConfig Config { get; } = config;
        public SourceStatus Status { get; set; } = new(config.Id, SourceState.Idle, null, 0, null);
        public SourceWatcher? Watcher { get; set; }
        public bool Disposed { get; set; }
    }

    private readonly ISaveWorldReader _reader;
    private readonly Bridge.IModBridgeReader? _bridge;
    private readonly IOwnedPalRepository _repository;
    private readonly CaptureProgressStore? _captures;
    private readonly SyncOptions _options;
    private readonly Action<string, Exception?>? _log;
    private readonly object _gate = new();
    private readonly List<Runtime> _sources = [];
    private readonly SemaphoreSlim _readLock = new(1, 1); // one heavy read at a time
    private bool _disposed;

    public SyncService(
        ISaveWorldReader reader,
        IOwnedPalRepository repository,
        SyncOptions? options = null,
        Action<string, Exception?>? log = null,
        Bridge.IModBridgeReader? bridge = null,
        CaptureProgressStore? captures = null)
    {
        _captures = captures;
        _bridge = bridge;
        _reader = reader;
        _repository = repository;
        _options = options ?? new SyncOptions();
        _log = log;
    }

    public IReadOnlyList<SourceStatus> Statuses
    {
        get { lock (_gate) return _sources.Select(s => s.Status).ToList(); }
    }

    public event EventHandler? StatusesChanged;

    public void ApplyConfig(IReadOnlyList<SourceConfig> sources)
    {
        var toRead = new List<Runtime>();
        var removedIds = new List<string>();
        lock (_gate)
        {
            if (_disposed) return;

            var wanted = sources.ToDictionary(s => s.Id, StringComparer.Ordinal);

            // Forget sources that are gone or whose settings changed (they are recreated below).
            foreach (var runtime in _sources.ToList())
            {
                if (wanted.TryGetValue(runtime.Config.Id, out var next) && next == runtime.Config) continue;
                Retire(runtime);
                _sources.Remove(runtime);
                if (!wanted.ContainsKey(runtime.Config.Id)) removedIds.Add(runtime.Config.Id);
            }

            var ordered = new List<Runtime>();
            foreach (var config in sources)
            {
                var runtime = _sources.FirstOrDefault(r => string.Equals(r.Config.Id, config.Id, StringComparison.Ordinal));
                if (runtime == null)
                {
                    runtime = new Runtime(config);
                    if (config.Enabled)
                    {
                        runtime.Watcher = CreateWatcher(runtime);
                        toRead.Add(runtime);
                    }
                }

                ordered.Add(runtime);
            }

            _sources.Clear();
            _sources.AddRange(ordered);
        }

        foreach (var id in removedIds) { _repository.RemoveSource(id); _captures?.Remove(id); }
        foreach (var disabled in sources.Where(s => !s.Enabled)) { _repository.RemoveSource(disabled.Id); _captures?.Remove(disabled.Id); }
        RaiseStatusesChanged();

        foreach (var runtime in toRead) _ = ReadAsync(runtime);
    }

    public async Task RefreshNowAsync(string? sourceId = null)
    {
        List<Runtime> targets;
        lock (_gate)
        {
            targets = _sources
                .Where(s => s.Config.Enabled && (sourceId == null || string.Equals(s.Config.Id, sourceId, StringComparison.Ordinal)))
                .ToList();
        }

        foreach (var runtime in targets)
        {
            runtime.Watcher?.CancelPending();
            await ReadAsync(runtime).ConfigureAwait(false);
        }
    }

    private SourceWatcher? CreateWatcher(Runtime runtime)
    {
        // Zip imports are a one-time copy: read on demand, never watched.
        if (runtime.Config.Type == SourceType.ZipImport) return null;
        // The mod already waits two seconds before it writes; the bridge file is read after a short pause so My Pals follows quickly.
        var options = runtime.Config.Type == SourceType.ModBridgeFile
            ? _options with { Debounce = _options.Debounce < TimeSpan.FromSeconds(1) ? _options.Debounce : TimeSpan.FromSeconds(1) }
            : _options;
        return new SourceWatcher(runtime.Config, options, () => _ = ReadAsync(runtime));
    }

    private static void Retire(Runtime runtime)
    {
        runtime.Disposed = true;
        runtime.Watcher?.Dispose();
    }

    private async Task ReadAsync(Runtime runtime)
    {
        await _readLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (runtime.Disposed || _disposed) return;

            var config = runtime.Config;
            var signature = SourceWatcher.ComputeSignature(config);
            SetStatus(runtime, runtime.Status with { State = SourceState.Reading, Error = null });

            try
            {
                var (pals, captures, worldName, worldId, bases) = await Task.Run(() => Read(config)).ConfigureAwait(false);
                if (runtime.Disposed) return;

                var status = new SourceStatus(config.Id, SourceState.Ok, DateTimeOffset.UtcNow, pals.Count, null) { WorldName = worldName, WorldId = worldId };
                _repository.PublishSourceSnapshot(config.Id, pals, status);
                _captures?.Publish(config.Id, captures);
                _captures?.PublishBases(config.Id, bases);
                runtime.Watcher?.SetReadSignature(signature);
                SetStatus(runtime, status);
            }
            catch (Exception ex)
            {
                if (runtime.Disposed) return;
                _log?.Invoke($"Reading source {config.Id} ({config.Path}) failed; keeping the previous data.", ex);

                // Remember the signature anyway: polling must not retry the same broken files every minute, but a
                // new change (the game finishing its write) will.
                runtime.Watcher?.SetReadSignature(signature);
                var message = ex is WorldUnavailableException unavailable ? unavailable.Key : ex.Message;
                var status = runtime.Status with { State = SourceState.Error, Error = message };
                _repository.SetSourceStatus(config.Id, status);
                SetStatus(runtime, status);
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    private (IReadOnlyList<OwnedPal> Pals, IReadOnlyList<PlayerCapture> Captures, string? WorldName, string? WorldId, IReadOnlyList<BaseSite> Bases) Read(SourceConfig config)
    {
        switch (config.Type)
        {
            case SourceType.GlobalStorageFile:
                var accountFolder = Directory.Exists(config.Path) ? config.Path : Path.GetDirectoryName(config.Path)!;
                return (_reader.ReadGlobalStorage(accountFolder).Select(p => p with { SourceId = config.Id }).ToList(), [], null, null, []);

            case SourceType.ModBridgeFile:
                if (_bridge == null) throw new InvalidOperationException("No mod bridge reader is configured.");
                var bridgeFile = Bridge.ModBridgeReader.EffectiveFile(config.Path);
                var (bridgePals, bridgeCaptures, bridgeBases) = _bridge.ReadAll(bridgeFile, config.Id);
                return (bridgePals, bridgeCaptures, null, Bridge.ModBridgeReader.Summarize(bridgeFile)?.WorldId, bridgeBases);

            default:
                var snapshot = _reader.ReadWorld(config.Path);
                return (snapshot.Pals.Select(p => p with { SourceId = config.Id }).ToList(), snapshot.Captures, snapshot.WorldName, snapshot.WorldId, snapshot.Bases);
        }
    }

    private void SetStatus(Runtime runtime, SourceStatus status)
    {
        lock (_gate) runtime.Status = status;
        RaiseStatusesChanged();
    }

    private void RaiseStatusesChanged() => StatusesChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var runtime in _sources) Retire(runtime);
        }
    }
}
