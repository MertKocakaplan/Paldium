using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>Reads data tables and L10N text tables from the game files as JSON objects.</summary>
public static class GameTables
{
    public const string TextFolder = "Pal/Content/L10N/{0}/Pal/DataTable/Text";

    /// <summary>The <c>Rows</c> object (row name to properties) of a data table asset.</summary>
    public static JObject LoadRows(DefaultFileProvider provider, string assetPath)
    {
        var resolved = GameProvider.ResolveAsset(provider, assetPath);
        var package = provider.LoadPackage(resolved);
        var exports = JArray.Parse(JsonConvert.SerializeObject(package.GetExports().ToList()));
        var table = exports.FirstOrDefault(e => e["Rows"] != null)
                    ?? throw new InvalidDataException($"{assetPath} is not a data table.");
        return (JObject)table["Rows"]!;
    }

    /// <summary>
    /// Key to text of an L10N text table (<c>TextData.LocalizedString</c>). Rows without text are skipped.
    /// <paramref name="language"/> is "en", "tr", ...
    /// </summary>
    public static Dictionary<string, string> LoadTexts(DefaultFileProvider provider, string language, string tableName)
    {
        var path = string.Format(TextFolder, language) + "/" + tableName;
        // Game text keys are FNames, which compare case-insensitively (PARTNERSKILL_Blueplatypus vs tribe BluePlatypus).
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in LoadRows(provider, path).Properties())
        {
            var text = row.Value["TextData"]?["LocalizedString"]?.Value<string>();
            if (!string.IsNullOrEmpty(text)) result[row.Name] = text;
        }

        return result;
    }

    /// <summary>"EPalElementType::Earth" becomes "Earth"; "None" and empty values become null.</summary>
    public static string? Enum(JToken? token)
    {
        var text = token?.Value<string>();
        if (string.IsNullOrEmpty(text)) return null;
        var index = text.LastIndexOf("::", StringComparison.Ordinal);
        var value = index >= 0 ? text[(index + 2)..] : text;
        return value is "None" or "no" ? null : value;
    }

    public static string? Name(JToken? token)
    {
        var text = token?.Value<string>();
        return string.IsNullOrEmpty(text) || text == "None" ? null : text;
    }
}
