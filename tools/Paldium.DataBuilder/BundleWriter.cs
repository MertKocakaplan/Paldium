using System.Text;
using System.Text.Json;
using Paldium.Core.Data.Schema;

namespace Paldium.DataBuilder;

/// <summary>Writes the data bundle files (UTF-8 without BOM, LF newlines).</summary>
public static class BundleWriter
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static void Write(string outDir, GameDataFile gameData, BreedingFile breeding)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "gamedata.json"),
            // Compact: with obtain.wild[].points the indented file would be about 15 MB (T33), compact is a fraction of that.
            JsonSerializer.Serialize(gameData, SchemaJson.Compact) + "\n", Utf8);
        File.WriteAllText(Path.Combine(outDir, "breeding.json"),
            JsonSerializer.Serialize(breeding, SchemaJson.Compact) + "\n", Utf8);
    }

    public static void WriteRules(string outDir, Paldium.Core.Breeding.BreedingRulesFile rules)
    {
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "breeding-rules.json"), JsonSerializer.Serialize(rules, SchemaJson.Compact) + "\n", Utf8);
    }

    /// <summary>
    /// The main world map picture (T101): copies <c>world-map.png</c> and <c>world-map.json</c> (the world bounds of the picture) from the
    /// extractor's output to <c>map/</c> of the bundle. Returns false when the extractor made no picture (the Paldex then shows the dots only).
    /// </summary>
    public static bool CopyWorldMap(string extractorDir, string outDir)
    {
        var png = Path.Combine(extractorDir, "world-map.png");
        var json = Path.Combine(extractorDir, "world-map.json");
        if (!File.Exists(png) || !File.Exists(json)) return false;
        var map = Path.Combine(outDir, "map");
        Directory.CreateDirectory(map);
        File.Copy(png, Path.Combine(map, "world-map.png"), overwrite: true);
        File.Copy(json, Path.Combine(map, "world-map.json"), overwrite: true);
        var large = Path.Combine(extractorDir, "world-map-large.png");
        if (File.Exists(large)) File.Copy(large, Path.Combine(map, "world-map-large.png"), overwrite: true);
        return true;
    }

    /// <summary>Copies the Ranch item icons (<c>icons/items/*.png</c>) of the extractor output (T108); returns how many.</summary>
    public static int CopyItemIcons(string extractorDir, string outDir) => CopyIconFolder(extractorDir, outDir, "items");

    /// <summary>Copies the building icons (<c>icons/buildings/*.png</c>, T128); returns how many.</summary>
    public static int CopyBuildingIcons(string extractorDir, string outDir) => CopyIconFolder(extractorDir, outDir, "buildings");

    private static int CopyIconFolder(string extractorDir, string outDir, string kind)
    {
        var source = Path.Combine(extractorDir, "icons", kind);
        if (!Directory.Exists(source)) return 0;
        var count = 0;
        foreach (var file in Directory.GetFiles(source, "*.png"))
        {
            CopyIcon(file, Path.Combine(outDir, "icons", kind, Path.GetFileName(file)));
            count++;
        }

        return count;
    }

    public static void CopyIcon(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
}
