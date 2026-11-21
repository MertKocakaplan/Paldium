using Microsoft.Extensions.Logging;
using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Data.Schema;

namespace Paldium.App.Services;

/// <summary>
/// Result of loading the data bundle at startup. When loading fails the UI shows an error page
/// instead of the normal layout, so pages can rely on <see cref="Data"/> being set.
/// </summary>
public sealed class AppState
{
    private readonly ILogger _log;

    private AppState(IGameData? data, IBreedingCalculator? breeding, string? error, string? errorDetail, DateTimeOffset checkedAtUtc, string dataFolder, ILogger log)
    {
        Data = data;
        Breeding = breeding;
        Error = error;
        ErrorDetail = errorDetail;
        CheckedAtUtc = checkedAtUtc;
        DataFolder = dataFolder;
        _log = log;
    }

    public IGameData? Data { get; private set; }
    public IBreedingCalculator? Breeding { get; private set; }

    /// <summary>Technical description of why the data bundle could not be loaded, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Exception type and message for diagnostic details on the error screen.</summary>
    public string? ErrorDetail { get; private set; }

    public DateTimeOffset CheckedAtUtc { get; private set; }

    public string DataFolder { get; }

    public int ExpectedSchemaVersion => SchemaJson.CurrentSchemaVersion;

    public bool IsReady => Error == null;

    public event EventHandler? Changed;

    public static AppState Load(string dataFolder, ILogger log)
    {
        var state = new AppState(null, null, null, null, DateTimeOffset.UtcNow, dataFolder, log);
        state.Reload();
        return state;
    }

    /// <summary>Attempts to reload the data bundle from <see cref="DataFolder"/> and notifies subscribers.</summary>
    public bool Reload()
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var data = GameDataLoader.Load(DataFolder);
            var breeding = BreedingCalculator.Load(data, DataFolder);
            Data = data;
            Breeding = breeding;
            Error = null;
            ErrorDetail = null;
            CheckedAtUtc = now;
            _log.LogInformation("Data bundle loaded from {Folder}: {Pals} Pals, game version {Version}",
                DataFolder, data.Pals.Count, data.Meta.GameVersion);
        }
        catch (Exception ex)
        {
            Data = null;
            Breeding = null;
            Error = ex.Message;
            ErrorDetail = $"{ex.GetType().Name}: {ex.Message}";
            CheckedAtUtc = now;
            _log.LogError(ex, "Data bundle could not be loaded from {Folder}", DataFolder);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return IsReady;
    }
}
