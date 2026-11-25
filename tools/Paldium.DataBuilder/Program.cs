using Paldium.DataBuilder;

// Usage: Paldium.DataBuilder --palcalc <dir> --out <dir> [--extractor <dir>] [--world <dir>] [--report <file>] [--timestamp <ISO-8601 UTC>]
string? palcalc = null, outDir = null, timestamp = null, extractorDir = null, worldDir = null, reportPath = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--palcalc" when i + 1 < args.Length: palcalc = args[++i]; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--extractor" when i + 1 < args.Length: extractorDir = args[++i]; break;
        case "--world" when i + 1 < args.Length: worldDir = args[++i]; break;
        case "--report" when i + 1 < args.Length: reportPath = args[++i]; break;
        case "--timestamp" when i + 1 < args.Length: timestamp = args[++i]; break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete argument: {args[i]}");
            return 2;
    }
}

if (palcalc == null || outDir == null)
{
    Console.Error.WriteLine("Usage: Paldium.DataBuilder --palcalc <dir> --out <dir> [--extractor <dir>] [--world <dir>] [--report <file>] [--timestamp <ISO-8601 UTC>]");
    return 2;
}

timestamp ??= DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
var tagsPath = Path.Combine(AppContext.BaseDirectory, "passive-tags.json");

var source = new PalCalcSource(palcalc);
var commit = PalCalcSource.GitCommit(palcalc);
var extractor = extractorDir == null ? null : new ExtractorSource(extractorDir, Path.Combine(AppContext.BaseDirectory, "region-map.json"));
var (gameData, breeding) = source.Build(timestamp, commit, tagsPath, outDir, extractor);
BundleWriter.Write(outDir, gameData, breeding);
if (extractor?.BreedingRules() is { } rules) BundleWriter.WriteRules(outDir, rules);
if (extractorDir != null) Console.WriteLine($"Building icons copied: {BundleWriter.CopyBuildingIcons(extractorDir, outDir)}.");
if (extractorDir != null) Console.WriteLine($"Item icons copied: {BundleWriter.CopyItemIcons(extractorDir, outDir)}, Pals with Ranch drops: {gameData.Pals.Count(p => p.Ranch is { Count: > 0 })}, Shops: {gameData.Shops?.Count ?? 0}.");
if (extractorDir != null) Console.WriteLine($"Map icons copied: {BundleWriter.CopyMapIcons(extractorDir, outDir)}.");
if (extractorDir != null) Console.WriteLine(BundleWriter.CopyWorldMap(extractorDir, outDir) ? "World & tree map pictures copied." : "No world map picture in the extractor output.");
if (extractorDir != null && worldDir != null)
{
    var mapPoints = MapPointsBuilder.Build(extractorDir, worldDir, gameData);
    BundleWriter.WriteMapPoints(outDir, mapPoints);
    Console.WriteLine($"Map points built: {mapPoints.Points.Count} points, {mapPoints.Regions.Count} regions, {mapPoints.Categories.Count} categories.");
}

Console.WriteLine($"PalCalc commit {commit}, db {source.DbVersion}");
Console.WriteLine($"Pals: {gameData.Pals.Count}, passives: {gameData.Passives.Count}, actives: {gameData.Actives.Count}, " +
                  $"elements: {gameData.Elements.Count}, languages: {gameData.Languages.Count}");
Console.WriteLine($"Breeding matrix: {breeding.Children.Count} slots, gender-specific rows: {breeding.GenderSpecific.Count}");
if (source.MissingIcons.Count > 0) Console.WriteLine("Missing pal icons: " + string.Join(", ", source.MissingIcons));
if (source.UnmappedPassives.Count > 0) Console.WriteLine("Passives without tag mapping (tagged Other): " + string.Join(", ", source.UnmappedPassives));
foreach (var w in source.Warnings) Console.WriteLine("Warning: " + w);
if (extractor != null)
{
    Console.WriteLine($"Game table rows merged: {extractor.MatchedSpecies}");
    Console.WriteLine("PalCalc vs game differences (game wins): " +
                      (extractor.DifferenceCounts.Count == 0 ? "none" : string.Join(", ", extractor.DifferenceCounts.Select(kv => $"{kv.Key} {kv.Value}"))));
    Console.WriteLine($"Partner skills without en/tr name: {extractor.PartnerSkillMissingNames.Count}");
    Console.WriteLine($"Species with OilExtraction work: {extractor.OilExtractionSpecies.Count}");
    Console.WriteLine($"Passives with negative effect but no penaltyTag: {extractor.PenaltyTagMismatches.Count}");
    Console.WriteLine($"Game terms: {gameData.Terms?.Count ?? 0}");
    if (extractor.ObtainData is { } ob)
    {
        Console.WriteLine($"Obtain: wild records {ob.WildRecords} ({ob.WildPoints} points), boss records {ob.BossRecords}, dungeon records {ob.DungeonRecords}, points outside both maps {ob.PointsOutsideMaps}");
        Console.WriteLine("Unmapped regions (wild records): " + (ob.UnmappedRegions.Count == 0 ? "none" : string.Join(", ", ob.UnmappedRegions.Select(kv => $"{kv.Key} {kv.Value}"))));
        Console.WriteLine($"Obtain skipped: zero-weight rows {ob.SkippedZeroWeightRows}, placeholder slots {ob.SkippedPlaceholderSlots}, " +
                          $"spawner names without placement {ob.UnmatchedSpawners.Count} ({ob.UnmatchedSpawnerRows} rows, {ob.UnmatchedFieldBossRows} FieldBoss), unrecognized Pal ids {ob.UnrecognizedPalIds.Count}");
    }
    if (reportPath != null)
    {
        var lines = new List<string> { "# Cross-check report" };
        void Section(string title, IEnumerable<string> items) { lines.Add(""); lines.Add("## " + title); lines.AddRange(items.Select(i => "- " + i)); }
        Section("PalCalc vs game differences", extractor.Differences);
        Section("Partner skills without en/tr name", extractor.PartnerSkillMissingNames);
        Section("OilExtraction species", extractor.OilExtractionSpecies);
        Section("Negative effect without penaltyTag", extractor.PenaltyTagMismatches);
        if (extractor.ObtainData is { } ob2)
        {
            Section("Unrecognized Pal ids in spawner rows (id: slots)", ob2.UnrecognizedPalIds.Select(kv => $"{kv.Key}: {kv.Value}"));
            Section("Spawner names without placement (spawner: Pals)",
                ob2.UnmatchedSpawnerPals.Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}"));
        }

        File.WriteAllText(reportPath, string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));
    }
}
return 0;
