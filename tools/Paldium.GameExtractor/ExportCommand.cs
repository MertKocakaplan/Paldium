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

    public static void Run(DefaultFileProvider provider, string paksFolder, string usmapPath, string outFolder)
    {
        Directory.CreateDirectory(outFolder);
        var warnings = new List<string>();

        Console.WriteLine("Reading texts (en, tr)...");
        var texts = TextExporter.Load(provider, warnings);

        Console.WriteLine("Reading species...");
        var species = SpeciesExporter.Export(provider, texts, warnings);

        Console.WriteLine("Reading passive effects...");
        var passives = PassiveEffectsExporter.Export(provider);

        Console.WriteLine("Reading breeding rules...");
        var rules = BreedingRulesExporter.Export(provider, species, warnings);

        Console.WriteLine("Reading obtain sources...");
        var obtain = ObtainExporter.Export(provider, warnings);

        Console.WriteLine("Reading Ranch drops and item icons...");
        var ranch = RanchExporter.Export(provider, outFolder, texts, warnings);

        Console.WriteLine("Reading Pal Effigy enhancements...");
        var relics = RelicExporter.Export(provider, outFolder, texts, warnings);

        Console.WriteLine("Reading the technology tree...");
        var technologies = TechnologyExporter.Export(provider, outFolder, texts, warnings);

        var terms = LocalizationExporter.Export(texts, warnings);
        Console.WriteLine("Reading the world map picture...");
        var worldMap = MapExporter.Export(provider, outFolder, warnings);

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
        Write(outFolder, "terms.json", terms);
        Write(outFolder, "texts.json", JObject.FromObject(texts));
        if (worldMap != null) Write(outFolder, "world-map.json", worldMap);
        Write(outFolder, "meta.json", meta);
        foreach (var warning in warnings) Console.WriteLine("Note: " + warning);
    }

    private static void Write(string folder, string name, JToken content)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content.ToString(Formatting.Indented) + "\n", Utf8);
        Console.WriteLine($"{name} ({new FileInfo(path).Length / 1024} KB)");
    }
}
