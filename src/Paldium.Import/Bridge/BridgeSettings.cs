using System.Text.Json;

namespace Paldium.Import.Bridge;

/// <summary>
/// The mod's keyboard shortcuts and scan mode, written by the app to <c>bridge\settings.json</c> (Settings > Mod shortcuts) and read by the
/// mod once at game start-up, so a change takes effect after the game is restarted (ARCHITECTURE 6.5).
/// A shortcut is a UE4SS key name with optional modifiers: <c>F9</c>, <c>Ctrl+F9</c>, <c>Ctrl+Shift+K</c>.
/// </summary>
public sealed record BridgeSettings
{
    public const string FileName = "settings.json";
    public const string Smart = "smart";
    public const string Full = "full";

    /// <summary>Keys the app offers (names from the UE4SS <c>Key</c> table): F1-F12 and A-Z.</summary>
    public static IReadOnlyList<string> KnownKeys { get; } =
        [.. Enumerable.Range(1, 12).Select(i => "F" + i), .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString())];

    public static IReadOnlyList<string> Modifiers { get; } = ["Ctrl", "Shift", "Alt"];

    public string ScanKey { get; init; } = "F9";
    public string ProbeKey { get; init; } = "F10";
    public string ClearKey { get; init; } = "F11";


    /// <summary>"smart" stops after five empty pages in a row; "full" goes through every page.</summary>
    public string ScanMode { get; init; } = Smart;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Parses the file; throws <see cref="InvalidDataException"/> for broken JSON. Missing fields keep their defaults.</summary>
    public static BridgeSettings Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<BridgeSettings>(json, Options) ?? throw new InvalidDataException("The settings file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The settings file is not valid: " + ex.Message, ex);
        }
    }

    /// <summary>Splits "Ctrl+Shift+F9" into its modifiers (in the canonical order) and the key; null for an unknown name.</summary>
    public static (IReadOnlyList<string> Modifiers, string Key)? TryParseShortcut(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var modifiers = new List<string>();
        string? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var modifier = Modifiers.FirstOrDefault(m => string.Equals(m, part, StringComparison.OrdinalIgnoreCase)
                || (m == "Ctrl" && string.Equals(part, "Control", StringComparison.OrdinalIgnoreCase)));
            if (modifier != null)
            {
                if (!modifiers.Contains(modifier)) modifiers.Add(modifier);
            }
            else if (key == null && KnownKeys.FirstOrDefault(k => string.Equals(k, part, StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                key = known;
            }
            else
            {
                return null;
            }
        }

        return key == null ? null : (Modifiers.Where(modifiers.Contains).ToList(), key);
    }

    /// <summary>The canonical text of a shortcut ("Ctrl+F9"), or null when it is not valid.</summary>
    public static string? Normalize(string? text) =>
        TryParseShortcut(text) is { } parsed ? string.Join('+', [.. parsed.Modifiers, parsed.Key]) : null;

    public static string Compose(IEnumerable<string> modifiers, string key) =>
        string.Join('+', [.. Modifiers.Where(m => modifiers.Contains(m)), key]);

    /// <summary>Problems that stop the file from being saved: <c>invalid:&lt;setting&gt;</c> or <c>duplicate</c>.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var shortcuts = new Dictionary<string, string?>
        {
            ["scanKey"] = Normalize(ScanKey), ["probeKey"] = Normalize(ProbeKey), ["clearKey"] = Normalize(ClearKey),
        };
        problems.AddRange(shortcuts.Where(s => s.Value == null).Select(s => "invalid:" + s.Key));
        var valid = shortcuts.Values.Where(v => v != null).Select(v => v!.ToUpperInvariant()).ToList();
        if (valid.Count != valid.Distinct().Count()) problems.Add("duplicate");
        if (ScanMode is not (Smart or Full)) problems.Add("invalid:scanMode");
        return problems;
    }
}

/// <summary>Reads and writes <c>bridge\settings.json</c>.</summary>
public sealed class BridgeSettingsStore
{
    private readonly string _path;
    private readonly string? _legacyPath;

    public BridgeSettingsStore(string? folder = null)
    {
        _path = Path.Combine(folder ?? Path.GetDirectoryName(ModBridgeReader.DefaultFile)!, BridgeSettings.FileName);
        _legacyPath = folder == null ? Path.Combine(Path.GetDirectoryName(ModBridgeReader.LegacyFile)!, BridgeSettings.FileName) : null;
    }

    public string FilePath => _path;

    /// <summary>The saved settings; the defaults when the file is missing or broken.</summary>
    public BridgeSettings Load()
    {
        try
        {
            var path = !File.Exists(_path) && _legacyPath != null && File.Exists(_legacyPath) ? _legacyPath : _path;
            return File.Exists(path) ? BridgeSettings.Parse(BridgeFileIo.ReadAllText(path)) : new BridgeSettings();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new BridgeSettings();
        }
    }

    /// <summary>Writes the file atomically. Throws <see cref="ArgumentException"/> when <see cref="BridgeSettings.Validate"/> finds a problem.</summary>
    public void Save(BridgeSettings settings)
    {
        var problems = settings.Validate();
        if (problems.Count > 0) throw new ArgumentException("Invalid mod settings: " + string.Join(", ", problems));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, settings.ToJson());
        File.Move(temp, _path, overwrite: true);
    }
}
