using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Queries;
using Paldium.Import.Sync;

namespace Paldium.App.Services;

/// <summary>User settings stored in <c>%APPDATA%\Paldium\settings.json</c> (ARCHITECTURE K10, 4.7).</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>UI language: "en" or "tr".</summary>
    public string Language { get; init; } = "en";

    /// <summary>Paldex view, filters and sort order, restored when the page is opened again.</summary>
    public PaldexSettings Paldex { get; init; } = new();

    /// <summary>My Pals view mode ("table", "cards", or "dense"), persisted across sessions.</summary>
    public MyPalsSettings MyPals { get; init; } = new();

    /// <summary>Which of the user's Pals count for planning (ARCHITECTURE 6.2).</summary>
    public ScopeSettings Scope { get; init; } = new();

    /// <summary>Configured Pal sources (ARCHITECTURE 4.7).</summary>
    public IReadOnlyList<SourceConfig> Sources { get; init; } = [];

    /// <summary>The last planner query (ARCHITECTURE 8: "last query is kept in the settings").</summary>
    public PlannerSettings Planner { get; init; } = new();

    /// <summary>Legacy: the first run used to add the local Global Palbox automatically (removed in T37; kept so old files still load).</summary>
    public bool SourcesInitialized { get; init; }

    /// <summary>True once the one-time T37 migration ran: the automatic Global Palbox source is gone and GlobalStorage is out of the scope.</summary>
    public bool GlobalPalboxMigrated { get; init; }

    /// <summary>True once the mod bridge file has been added automatically (it is offered only once; removing the source keeps it removed).</summary>
    public bool BridgeOffered { get; init; }

    /// <summary>Widths in pixels of the resizable left panels by page ("planner", "mypals", "skills", "paldex"; T102).</summary>
    public IReadOnlyDictionary<string, int> PanelWidths { get; init; } = new Dictionary<string, int>();

    /// <summary>The Palworld folder the user picked by hand for the mod install button (null: it is looked up through the Steam manifest each time).</summary>
    public string? PalworldFolderPicked { get; init; }

    /// <summary>Update check choices and bookkeeping (T131).</summary>
    public UpdateSettings Update { get; init; } = new();
}

/// <summary>The update check (screens/update.md): the switch, the skipped version and when the last check happened.</summary>
public sealed record UpdateSettings
{
    /// <summary>"Check for updates at startup" (default on). Off: only "Check now" asks GitHub.</summary>
    public bool AutoCheck { get; init; } = true;

    /// <summary>A version the user chose to skip: no badge for it; a newer one shows again.</summary>
    public string? SkippedVersion { get; init; }

    public DateTimeOffset? LastCheckUtc { get; init; }

    /// <summary>The newer version found at the last check (null: up to date).</summary>
    public string? LastSeenVersion { get; init; }

    /// <summary>After a rate limit answer: no automatic check before this time.</summary>
    public DateTimeOffset? NextAutoCheckUtc { get; init; }

    /// <summary>The version that ran last; a higher running version means the app was just updated.</summary>
    public string? LastRunVersion { get; init; }
}

public sealed record ScopeSettings
{
    public const string Me = "me";
    public const string Selected = "selected";
    public const string All = "all";

    /// <summary>"me", "selected" or "all".</summary>
    public string Mode { get; init; } = Me;

    /// <summary>Player id of the user (the "I am" choice); without it "me" behaves like "all".</summary>
    public string? MyPlayerId { get; init; }

    public IReadOnlyList<string> SelectedPlayerIds { get; init; } = [];

    /// <summary>Base and Dimensional Pal Storage Pals count for the chosen players even when another guild member owns them (default on).</summary>
    public bool ShareGuildLocations { get; init; } = true;

    /// <summary>Locations that count as owned Pals. The Global Palbox is only a genetic copy (T37) and is off by default.</summary>
    public IReadOnlyList<LocationKind> Locations { get; init; } = OwnerScope.AllLocations.Where(l => l != LocationKind.GlobalStorage).ToList();
}

public sealed record PlannerSettings
{
    public string? TargetId { get; init; }

    /// <summary>"none", "owned" or "species".</summary>
    public string StartMode { get; init; } = "none";

    public string? StartOwnedUid { get; init; }
    public string? StartSpeciesId { get; init; }
    public IReadOnlyList<string> PreferredUids { get; init; } = [];
    public bool AllowWild { get; init; } = true;
    public int MaxWild { get; init; } = 2;
    public bool ExcludeLegendaryWild { get; init; } = true;
    public int MaxSteps { get; init; } = 6;

    /// <summary>Wanted passives (0..4); with at least one the passive solver runs, otherwise the species path finder.</summary>
    public IReadOnlyList<string> DesiredPassives { get; init; } = [];

    public int MaxUnwanted { get; init; }

    /// <summary>Wanted gender of the Pal the plan makes: "any", "male" or "female" (both planners).</summary>
    public string TargetGender { get; init; } = "any";

    /// <summary>Unwanted passives assumed on a wild Pal.</summary>
    public int WildAssumedUnwanted { get; init; } = 1;

    public bool AllowSpecialCake { get; init; }

    /// <summary>True: plans must use at least one preferred Pal.</summary>
    public bool RequirePreferred { get; init; }

    public int TimeLimitSeconds { get; init; } = 30;

    /// <summary>Exhaustive passive search (slower, never narrows the search space).</summary>
    public bool Exhaustive { get; init; }

    /// <summary>T107: single Pals (UID), species and groups no plan may use. Part of the query: kept when the target changes.</summary>
    public IReadOnlyList<string> ExcludedUids { get; init; } = [];

    public IReadOnlyList<string> ExcludedSpecies { get; init; } = [];

    public IReadOnlyList<string> ExcludedGroups { get; init; } = [];
}

public sealed record PaldexSettings
{
    /// <summary>"table", "cards", or "dense".</summary>
    public string View { get; init; } = "cards";

    public bool FiltersOpen { get; init; }

    public PalFilter Filter { get; init; } = new();

    public PalSort Sort { get; init; } = new();
}

public sealed record MyPalsSettings
{
    /// <summary>"table", "cards", or "dense".</summary>
    public string View { get; init; } = "table";
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Writes go to a temporary file that is then moved over the
/// real one, so a crash never leaves a half-written settings file. A missing or unreadable file yields defaults.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly ILogger<SettingsStore>? _log;
    private AppSettings _current;

    public SettingsStore(string? path = null, ILogger<SettingsStore>? log = null)
    {
        Path = path ?? DefaultPath;
        _log = log;
        _current = Load();
    }

    public static string DefaultPath => System.IO.Path.Combine(Paldium.Import.Migration.AppFolders.Roaming, "settings.json");

    public string Path { get; }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event EventHandler<AppSettings>? Changed;

    /// <summary>Applies the change, saves it atomically and raises <see cref="Changed"/>.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;
        lock (_gate)
        {
            updated = change(_current);
            Save(updated);
            _current = updated;
        }

        Changed?.Invoke(this, updated);
    }

    private AppSettings Load()
    {
        if (!File.Exists(Path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), JsonOptions);
            return settings ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Settings file {Path} could not be read; using defaults", Path);
            TryBackup();
            return new AppSettings();
        }
    }

    private void TryBackup()
    {
        try
        {
            File.Move(Path, Path + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not back up unreadable settings file {Path}", Path);
        }
    }

    private void Save(AppSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, Path, overwrite: true);
    }
}
