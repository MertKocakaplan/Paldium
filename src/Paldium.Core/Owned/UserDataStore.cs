using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Paldium.Core.Domain;

namespace Paldium.Core.Owned;

/// <summary>
/// Persistence of the user-owned Pal data: <c>manual-pals.json</c> and <c>overrides.json</c> in the user data folder
/// (ARCHITECTURE 4.7). Writes go to a temporary file that is moved over the real one. An unreadable file is moved
/// to <c>*.bak</c> and the store starts empty.
/// </summary>
public sealed class UserDataStore
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Action<string, Exception?>? _warn;

    public UserDataStore(string folder, Action<string, Exception?>? warn = null)
    {
        Folder = folder;
        _warn = warn;
    }

    public string Folder { get; }

    public string ManualPalsPath => Path.Combine(Folder, "manual-pals.json");

    public string OverridesPath => Path.Combine(Folder, "overrides.json");

    public string PresetsPath => Path.Combine(Folder, "presets.json");

    /// <summary>A passive set the user saved (name and up to four passive ids).</summary>
    public sealed record SavedPreset(string Name, List<string> PassiveIds);

    private sealed record PresetsFile(int SchemaVersion, List<SavedPreset> Presets);

    private sealed record ManualFile(int SchemaVersion, List<OwnedPal> Pals);

    private sealed record OverridesFile(int SchemaVersion, Dictionary<string, PalOverride> Overrides);

    public List<OwnedPal> LoadManualPals() =>
        Load<ManualFile>(ManualPalsPath)?.Pals ?? [];

    public List<SavedPreset> LoadPresets() => Load<PresetsFile>(PresetsPath)?.Presets ?? [];

    public void SavePresets(IEnumerable<SavedPreset> presets) =>
        Save(PresetsPath, new PresetsFile(SchemaVersion, presets.ToList()));

    public Dictionary<string, PalOverride> LoadOverrides() =>
        new(Load<OverridesFile>(OverridesPath)?.Overrides ?? [], StringComparer.Ordinal);

    public void SaveManualPals(IEnumerable<OwnedPal> pals) =>
        Save(ManualPalsPath, new ManualFile(SchemaVersion, pals.ToList()));

    public void SaveOverrides(IReadOnlyDictionary<string, PalOverride> overrides) =>
        Save(OverridesPath, new OverridesFile(SchemaVersion, new Dictionary<string, PalOverride>(overrides, StringComparer.Ordinal)));

    private T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            var version = value switch
            {
                ManualFile m => m.SchemaVersion,
                OverridesFile o => o.SchemaVersion,
                PresetsFile p => p.SchemaVersion,
                _ => SchemaVersion,
            };
            if (value == null || version != SchemaVersion)
                throw new InvalidDataException($"Unsupported or empty user data file (schema version {version}).");
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _warn?.Invoke($"User data file {path} could not be read; starting empty and keeping a .bak copy.", ex);
            TryBackup(path);
            return null;
        }
    }

    private void TryBackup(string path)
    {
        try
        {
            File.Move(path, path + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"Could not back up unreadable user data file {path}.", ex);
        }
    }

    private void Save<T>(string path, T content)
    {
        Directory.CreateDirectory(Folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(content, Options));
        File.Move(temp, path, overwrite: true);
    }
}
