using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// The names and icons of items (<c>DT_ItemDataTable</c>, <c>DT_ItemIconDataTable</c>): an item's <c>OverrideName</c> names it when set
/// (the same rule as the technology tree). Icons are saved once into <c>icons/items/</c> of the output.
/// </summary>
public sealed class ItemCatalog
{
    private const string ItemIconTable = "Pal/Content/Pal/DataTable/Item/DT_ItemIconDataTable";

    private readonly DefaultFileProvider _provider;
    private readonly string _outFolder;
    private readonly List<string> _warnings;
    private readonly Dictionary<string, JObject> _rows;
    private readonly Dictionary<string, JObject> _icons;
    private readonly Dictionary<string, string?> _saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _names;

    public ItemCatalog(DefaultFileProvider provider, string outFolder,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, List<string> warnings)
    {
        _provider = provider;
        _outFolder = outFolder;
        _warnings = warnings;
        _rows = Map(GameTables.LoadRows(provider, SpeciesExporter.ItemTable));
        _icons = Map(GameTables.LoadRows(provider, ItemIconTable));
        _names = TextExporter.Languages.ToDictionary(language => language, language =>
        {
            var names = new Dictionary<string, string>(texts[language]["itemName"], StringComparer.OrdinalIgnoreCase);
            foreach (var (id, row) in _rows)
            {
                if (GameTables.Name(row["OverrideName"]) is { } over && names.TryGetValue(over, out var text))
                    names["ITEM_NAME_" + id] = text;
            }
            return names;
        }, StringComparer.Ordinal);
    }

    public JObject? Row(string itemId) => _rows.TryGetValue(itemId, out var row) ? row : null;

    /// <summary>language -> name; a language without a text is left out.</summary>
    public JObject Names(string itemId)
    {
        var names = new JObject();
        foreach (var language in TextExporter.Languages)
        {
            if (_names[language].TryGetValue("ITEM_NAME_" + itemId, out var name) && !string.IsNullOrWhiteSpace(name))
                names[language] = name.Trim();
        }
        return names;
    }

    /// <summary>Saves the item's icon and returns its bundle path (<c>icons/items/X.png</c>), or null when there is none.</summary>
    public string? Icon(string itemId)
    {
        var iconKey = (Row(itemId) is { } row ? GameTables.Name(row["IconName"]) : null) ?? itemId;
        var key = _icons.ContainsKey(iconKey) ? iconKey : _icons.ContainsKey(itemId) ? itemId : iconKey;
        if (_saved.TryGetValue(key, out var saved)) return saved;

        string? path = null;
        var asset = _icons.TryGetValue(key, out var entry) ? entry["Icon"]?["AssetPathName"]?.Value<string>() : null;
        if (!string.IsNullOrEmpty(asset) && asset != "None")
        {
            var folder = Path.Combine(_outFolder, "icons", "items");
            Directory.CreateDirectory(folder);
            if (RanchExporter.SaveIcon(_provider, asset, Path.Combine(folder, key + ".png"), _warnings))
                path = $"icons/items/{key}.png";
        }

        _saved[key] = path;
        return path;
    }

    private static Dictionary<string, JObject> Map(JObject rows) =>
        rows.Properties().ToDictionary(p => p.Name, p => (JObject)p.Value, StringComparer.OrdinalIgnoreCase);
}
