using System.Text;
using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Paldium.GameExtractor.Exporters;

namespace Paldium.GameExtractor;

/// <summary>
/// <c>export --out &lt;dir&gt;</c>: writes normalized JSON for the DataBuilder (<c>species.json</c>,
/// <c>passive-effects.json</c>, <c>obtain.json</c>, <c>texts.json</c>, <c>meta.json</c>). Only Pal-Windows.pak is read.
/// </summary>
public static class ExportCommand
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static void Run(DefaultFileProvider provider, string paksFolder, string usmapPath, string outFolder, string? worldDir = null)
    {
        Directory.CreateDirectory(outFolder);
        var warnings = new List<string>();
        var worldActorsPath = ResolveWorldActorsPath(worldDir);

        Console.WriteLine("Reading texts (en, tr)...");
        var texts = TextExporter.Load(provider, warnings);

        Console.WriteLine("Reading species...");
        var species = SpeciesExporter.Export(provider, texts, warnings);

        Console.WriteLine("Reading passive effects...");
        var passives = PassiveEffectsExporter.Export(provider);

        Console.WriteLine("Reading breeding rules...");
        var rules = BreedingRulesExporter.Export(provider, species, warnings);

        Console.WriteLine("Reading obtain sources...");
        var obtain = ObtainExporter.Export(provider, warnings, worldActorsPath);

        Console.WriteLine("Reading Ranch drops and item icons...");
        var ranch = RanchExporter.Export(provider, outFolder, texts, warnings);

        Console.WriteLine("Reading Pal Effigy enhancements...");
        var relics = RelicExporter.Export(provider, outFolder, texts, warnings);

        Console.WriteLine("Reading the technology tree...");
        var technologies = TechnologyExporter.Export(provider, outFolder, texts, warnings);

        Console.WriteLine("Reading merchant shops...");
        var shops = ShopExporter.Export(provider, outFolder, texts, warnings, worldActorsPath);

        var terms = LocalizationExporter.Export(texts, warnings);
        Console.WriteLine("Reading the world map pictures and icons...");
        var worldMap = MapExporter.Export(provider, outFolder, warnings);
        var treeMap = MapExporter.ExportTree(provider, outFolder, warnings);
        MapIconExporter.Export(provider, outFolder, warnings);
        MapIconExporter.ExportItemIcons(provider, outFolder, MapTablesExporter.MaterialItems.Select(m => m.ItemId).Append("DogCoin"), warnings);
        var mapTables = MapTablesExporter.Export(provider, texts, warnings);

        Console.WriteLine("Reading quests and ancient towers...");
        var items = new ItemCatalog(provider, outFolder, texts, warnings);
        var quests = QuestExporter.Export(provider, items, texts, warnings, worldActorsPath);
        var pickups = PickupTowerExporter.Export(provider, items, warnings);

        var meta = new JObject
        {
            ["generatedAtUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["pakFile"] = GameProvider.MainPakName,
            ["pakSizeBytes"] = new FileInfo(Path.Combine(paksFolder, GameProvider.MainPakName)).Length,
            ["usmapFile"] = Path.GetFileName(usmapPath),
            ["languages"] = new JArray(TextExporter.Languages),
            ["partnerSkillNameKey"] = SpeciesExporter.NameKeyPattern,
            ["partnerSkillDescriptionKey"] = SpeciesExporter.DescriptionKeyPattern,
            ["warnings"] = new JArray(warnings),
        };

        Write(outFolder, "species.json", species);
        Write(outFolder, "passive-effects.json", passives);
        Write(outFolder, "obtain.json", obtain);
        Write(outFolder, "breeding-rules.json", rules);
        Write(outFolder, "ranch.json", ranch);
        Write(outFolder, "relics.json", relics);
        Write(outFolder, "technologies.json", technologies);
        Write(outFolder, "shops.json", shops);
        Write(outFolder, "map-tables.json", mapTables);
        Write(outFolder, "quests.json", quests);
        Write(outFolder, "pickups.json", pickups);
        Write(outFolder, "terms.json", terms);
        Write(outFolder, "texts.json", JObject.FromObject(texts));
        if (worldMap != null) Write(outFolder, "world-map.json", worldMap);
        if (treeMap != null) Write(outFolder, "tree-map.json", treeMap);
        Write(outFolder, "meta.json", meta);
        foreach (var warning in warnings) Console.WriteLine("Note: " + warning);
    }

    private static string? ResolveWorldActorsPath(string? worldDir)
    {
        if (string.IsNullOrWhiteSpace(worldDir)) return null;
        if (File.Exists(worldDir)) return worldDir;
        var candidate = Path.Combine(worldDir, "world-actors.json");
        return File.Exists(candidate) ? candidate : null;
    }

    private static void Write(string folder, string name, JToken content)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content.ToString(Formatting.Indented) + "\n", Utf8);
        Console.WriteLine($"{name} ({new FileInfo(path).Length / 1024} KB)");
    }
}
