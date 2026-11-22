using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paldium.Import.Bridge;

/// <summary>States the mod reports in <c>bridge\status.json</c> while it scans (ARCHITECTURE 6.5).</summary>
public static class BridgeScanState
{
    public const string Idle = "idle";
    public const string WaitingForUi = "waitingForUi";
    public const string Scanning = "scanning";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";

    public static bool IsRunning(string state) => state is WaitingForUi or Scanning;
}

/// <summary>Progress of a "scan all" run (<c>bridge\status.json</c>, written by the mod).</summary>
public sealed record BridgeStatus
{
    public string State { get; init; } = BridgeScanState.Idle;
    public string? Kind { get; init; }
    public string? BoxOwnerUid { get; init; }
    public int? Page { get; init; }
    public int? PageCount { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Parses the file; throws <see cref="InvalidDataException"/> for broken JSON (the mod writes atomically, but a reader must still cope).</summary>
    public static BridgeStatus Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BridgeStatus>(json, Options) ?? throw new InvalidDataException("The status file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The status file is not valid: " + ex.Message, ex);
        }
    }
}

/// <summary>
/// Follows the progress of the mod's "scan all" (started with the shortcut in the game). It only reads <c>bridge\status.json</c>; the app never
/// talks to the game itself and has no scan button.
/// </summary>
public sealed class BridgeScanService : IDisposable
{
    public const string StatusFileName = "status.json";

    private readonly string _folder;
    private readonly string? _legacyFolder;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private string? _lastText;
    private bool _disposed;

    public BridgeScanService(string? folder = null, TimeSpan? pollInterval = null)
    {
        _folder = folder ?? Path.GetDirectoryName(ModBridgeReader.DefaultFile)!;
        _legacyFolder = folder == null ? Path.GetDirectoryName(ModBridgeReader.LegacyFile) : null;
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(700);
        _timer = new Timer(_ => Poll(), null, interval, interval);
    }

    /// <summary>The latest status the mod wrote (null: the file does not exist or is unreadable).</summary>
    public BridgeStatus? Current { get; private set; }

    /// <summary>Raised when the status changes; can come from any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>True when the mod is working on a scan (started in the game or by this app).</summary>
    public bool IsRunning => Current is { } status && BridgeScanState.IsRunning(status.State);

    /// <summary>Reads the status file once (also what the timer does).</summary>
    public void Poll()
    {
        lock (_gate)
        {
            if (_disposed) return;
            BridgeStatus? status = null;
            string? text = null;
            try
            {
                var path = Path.Combine(_folder, StatusFileName);
                if (!File.Exists(path) && _legacyFolder != null)
                {
                    var legacyPath = Path.Combine(_legacyFolder, StatusFileName);
                    if (File.Exists(legacyPath)) path = legacyPath;
                }
                if (File.Exists(path))
                {
                    text = BridgeFileIo.ReadAllText(path);
                    if (text == _lastText) return;
                    status = BridgeStatus.Parse(text);
                }
                else if (_lastText == null)
                {
                    return; // still no file
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return; // keep the last good status; the next poll tries again
            }

            _lastText = text;
            Current = status;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _timer.Dispose();
    }
}
