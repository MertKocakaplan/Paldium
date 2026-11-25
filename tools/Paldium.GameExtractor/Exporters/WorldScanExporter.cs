using System.Diagnostics;
using System.Globalization;
using System.Text;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// <c>scan-world --out &lt;dir&gt; [--grid MainGrid,oilrig,CloseRange]</c>: scans persistent <c>PL_MainWorld5.umap</c> and
/// World Partition cell packages under <c>PL_MainWorld5/_Generated_/</c> read-only, extracts placed actor classes,
/// world locations and gameplay properties, writes <c>world-actors.json</c> and <c>world-classes.txt</c>, and verifies
/// transforms against known tables.
/// </summary>
public static class WorldScanExporter
{
    private const string PersistentMapPath = "Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5.umap";
    private const string GeneratedPrefix = "Pal/Content/Pal/Maps/MainWorld_5/PL_MainWorld5/_Generated_/";
    private const string BossLocationTable = "Pal/Content/Pal/DataTable/UI/DT_BossSpawnerLoactionData";
    private const string QuestLocationTable = "Pal/Content/Pal/DataTable/Quest/DT_PalQuestLocationData";

    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Level", "Model", "World", "BP_PalWorldSettings_C", "InstancedFoliageActor",
        "NavigationSystemModuleConfig", "AbstractNavData", "RecastNavMesh",
        "Brush", "StaticMeshActor", "LODActor", "HLODActor",
        "LandscapeStreamingProxy", "Landscape", "AtmosphericFog", "ExponentialHeightFog",
        "SkyLight", "DirectionalLight", "PostProcessVolume", "ReflectionCapture",
        "SphereReflectionCapture", "BoxReflectionCapture", "PlanarReflection",
        "VolumetricCloud", "SkyAtmosphere", "RuntimeVirtualTextureVolume",
    };

    private static readonly string[] AllowlistedProps =
    [
        "LevelObjectInstanceId",
        "FieldLotteryName",
        "LotteryName",
        "SpawnerName",
        "SpawnGroupList",
        "SpawnerType",
        "FastTravelPointID",
        "FastTravelPointId",
        "LocationId",
        "DungeonName",
        "DungeonId",
        "SpawnAreaId",
        "BossType",
        "ShopId",
        "ItemShopId",
        "PalShopId",
        "LotteryDataName",
        "itemShopLotteryType",
        "itemShopSimpleLotteryTableName",
        "palShopLotteryType",
        "palShopSimpleLotteryTableName",
        "NpcId",
        "CharacterID",
        "CharacterId",
        "UniqueNPCID",
        "RelicType",
        "NoteId",
        "TextId",
        "MsgId",
        "MsgID",
        "AreaName",
        "MapObjectId",
        "StaticItemId",
        "ItemId",
        "Grade",
        "Level",
        "RespawnPointID",
        "SpawnPointID",
        "FixedDungeonId",
        "TowerType",
        "EnemyCampId",
        "UniqueName",
        "HumanName",
        "NPCName",
        "OtomoName",
        "DungeonNameRowHandle",
        "EnemySpawnerSoftClass",
        "NoteRowName",
        "CampSpawnerName",
        "ItemPickupRowName",
        "SoftUnlockMapMaskTexture",
        "BoxExtent",
        "SphereRadius",
        "SpawnerRuleClass",
        "RelatedDataLayerAsset",
    ];

    private static readonly string[] IgnoredPropKeys =
    [
        "RootComponent", "DefaultSceneRoot", "BlueprintCreatedComponents", "InstanceComponents",
        "UberGraphFrame", "UCSSerializationIndex", "bNetAddressable", "CreationMethod",
        "bAlwaysRelevant", "bReplicates", "NetDormancy", "bCanBeDamaged", "PrimaryActorTick",
        "bActorEnableCollision", "bOnlyRelevantToOwner", "bNetLoadOnClient", "SpriteScale",
        "NavigationSystemConfig", "BookmarkArray", "OverrideMaterials", "StaticMesh",
        "BodyInstance", "RelativeLocation", "RelativeRotation", "RelativeScale3D", "AttachParent",
    ];

    public static void Run(DefaultFileProvider provider, string outFolder, IReadOnlyList<string> grids)
    {
        Directory.CreateDirectory(outFolder);
        var sw = Stopwatch.StartNew();

        var gridPrefixes = grids
            .Select(g => g.Trim())
            .Where(g => g.Length > 0)
            .Select(g => g + "_")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (gridPrefixes.Length == 0) gridPrefixes = ["MainGrid_"];

        var cellFiles = new List<string>();
        if (provider.Files.ContainsKey(PersistentMapPath))
            cellFiles.Add(PersistentMapPath);

        cellFiles.AddRange(provider.Files.Keys
            .Where(k => k.StartsWith(GeneratedPrefix, StringComparison.OrdinalIgnoreCase)
                        && k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            .Where(k =>
            {
                var fileName = k[GeneratedPrefix.Length..];
                return gridPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            })
            .Order(StringComparer.Ordinal));

        Console.WriteLine($"Scanning {cellFiles.Count} packages (persistent map + grids: {string.Join(", ", grids)})...");

        var actorsByClass = new SortedDictionary<string, JArray>(StringComparer.Ordinal);
        var totalActors = 0;
        var failedCells = 0;

        for (var c = 0; c < cellFiles.Count; c++)
        {
            var cellPath = cellFiles[c];
            try
            {
                var package = provider.LoadPackage(cellPath);
                var exports = package.GetExports().ToList();
                var cellName = Path.GetFileNameWithoutExtension(cellPath);

                for (var i = 0; i < exports.Count; i++)
                {
                    var export = exports[i];
                    var cls = export.ExportType;
                    if (string.IsNullOrEmpty(cls) || SkipClasses.Contains(cls) || cls.EndsWith("Component", StringComparison.Ordinal))
                        continue;

                    if (!HasRootComponent(export)) continue;

                    var actorJson = SafeToJson(export);
                    if (actorJson == null) continue;

                    var propsObj = actorJson["Properties"] as JObject;
                    var rootPath = (propsObj?["RootComponent"] as JObject)?["ObjectPath"]?.Value<string>();
                    var loc = ResolveWorldLocation(exports, rootPath);
                    if (loc == null) continue;

                    var props = ExtractProperties(actorJson, exports, i, rootPath);
                    var entry = new JObject
                    {
                        ["cell"] = cellName,
                        ["x"] = Math.Round(loc.Value.X, 1),
                        ["y"] = Math.Round(loc.Value.Y, 1),
                        ["z"] = Math.Round(loc.Value.Z, 1),
                    };
                    if (props.Count > 0) entry["props"] = props;

                    if (!actorsByClass.TryGetValue(cls, out var arr))
                        actorsByClass[cls] = arr = [];
                    arr.Add(entry);
                    totalActors++;
                }
            }
            catch (Exception ex)
            {
                failedCells++;
                if (failedCells <= 5)
                    Console.Error.WriteLine($"Warning: cell {cellPath} failed: {ex.Message}");
            }

            if ((c + 1) % 1000 == 0)
                Console.WriteLine($"  {c + 1}/{cellFiles.Count} cells, {totalActors} actors...");
        }

        sw.Stop();
        var peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0);

        var actorsPath = Path.Combine(outFolder, "world-actors.json");
        var rootObj = new JObject();
        foreach (var (cls, arr) in actorsByClass)
            rootObj[cls] = arr;
        File.WriteAllText(actorsPath, rootObj.ToString(Formatting.None) + "\n", new UTF8Encoding(false));

        var classesLines = actorsByClass
            .Select(kv =>
            {
                var sample = (JObject)kv.Value[0];
                return (
                    Class: kv.Key,
                    Count: kv.Value.Count,
                    Sample: string.Create(CultureInfo.InvariantCulture, $"({sample["x"]?.Value<double>():F1}, {sample["y"]?.Value<double>():F1}, {sample["z"]?.Value<double>():F1})"));
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Class, StringComparer.Ordinal)
            .Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.Count,6}  {x.Class,-64}  {x.Sample}"))
            .ToList();

        var classesPath = Path.Combine(outFolder, "world-classes.txt");
        File.WriteAllLines(classesPath, classesLines, new UTF8Encoding(false));

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Scan done in {sw.Elapsed.TotalSeconds:0.1}s (peak memory {peakMb:0} MB): {cellFiles.Count} cells, {failedCells} failed, {actorsByClass.Count} classes, {totalActors} actors."));
        Console.WriteLine($"Wrote {actorsPath} ({new FileInfo(actorsPath).Length / 1024} KB) and {classesPath}.");

        VerifyPositions(provider, actorsByClass);
    }

    private static bool HasRootComponent(UObject export)
    {
        for (var i = 0; i < export.Properties.Count; i++)
        {
            if (string.Equals(export.Properties[i].Name.Text, "RootComponent", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static JObject? SafeToJson(UObject export)
    {
        try
        {
            return JObject.Parse(JsonConvert.SerializeObject(export));
        }
        catch
        {
            return null;
        }
    }

    private static (double X, double Y, double Z)? ResolveWorldLocation(List<UObject> exports, string? rootObjectPath)
    {
        if (string.IsNullOrEmpty(rootObjectPath)) return null;
        double x = 0, y = 0, z = 0;
        var foundAny = false;
        var visited = new HashSet<int>();
        var currentPath = rootObjectPath;

        for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(currentPath); depth++)
        {
            var idx = ParseExportIndex(currentPath);
            if (idx < 0 || idx >= exports.Count || !visited.Add(idx)) break;

            var comp = SafeToJson(exports[idx]);
            var props = comp?["Properties"] as JObject;
            if (props == null) break;

            if (props["RelativeLocation"] is JObject rel)
            {
                x += rel["X"]?.Value<double>() ?? 0;
                y += rel["Y"]?.Value<double>() ?? 0;
                z += rel["Z"]?.Value<double>() ?? 0;
                foundAny = true;
            }

            currentPath = (props["AttachParent"] as JObject)?["ObjectPath"]?.Value<string>();
        }

        return foundAny ? (x, y, z) : null;
    }

    private static int ParseExportIndex(string objectPath)
    {
        var dot = objectPath.LastIndexOf('.');
        return dot >= 0 && int.TryParse(objectPath[(dot + 1)..], out var idx) ? idx : -1;
    }

    private static JObject ExtractProperties(JObject actorJson, List<UObject> exports, int actorIndex, string? rootObjectPath)
    {
        var result = new JObject();
        if (actorJson["Properties"] is JObject props)
            CollectRelevantProps(props, result);

        // Some Blueprints store spawner/shop config on child components owned by the actor.
        var actorName = actorJson["Name"]?.Value<string>();
        if (!string.IsNullOrEmpty(actorName))
        {
            for (var i = 0; i < exports.Count; i++)
            {
                if (i == actorIndex) continue;
                var exp = exports[i];
                if (exp.Outer == null || !string.Equals(exp.Outer.Name.Text, actorName, StringComparison.Ordinal))
                    continue;
                if (exp.Properties.Count == 0) continue;
                if (!exp.Properties.Any(p => IsInterestingPropKey(p.Name.Text))) continue;
                var compJson = SafeToJson(exp);
                if (compJson?["Properties"] is JObject compProps)
                    CollectRelevantProps(compProps, result);
            }
        }

        return result;
    }

    private static void CollectRelevantProps(JObject props, JObject target)
    {
        foreach (var prop in props.Properties())
        {
            if (IgnoredPropKeys.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (!IsInterestingPropKey(prop.Name)) continue;
            if (prop.Value.Type is JTokenType.Null) continue;
            target[prop.Name] = prop.Value;
        }
    }

    private static bool IsInterestingPropKey(string key)
    {
        if (AllowlistedProps.Contains(key, StringComparer.OrdinalIgnoreCase)) return true;
        foreach (var kw in InterestingKeywords)
        {
            if (key.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly string[] InterestingKeywords =
    [
        "Spawner", "SpawnGroup", "Lottery", "FastTravel", "TravelPoint", "Dungeon",
        "Shop", "Vendor", "Vender", "Npc", "CharacterId", "CharacterID", "Boss",
        "Relic", "Note", "Quest", "LevelObjectInstanceId", "MapObject", "Area",
        "Region", "Respawn", "SpawnPoint", "Tower", "Camp", "Treasure", "Egg",
        "TextId", "MsgId", "DisplayName", "Title", "Grade", "Biome", "Field",
    ];

    private static void VerifyPositions(DefaultFileProvider provider, SortedDictionary<string, JArray> actorsByClass)
    {
        Console.WriteLine("\n=== Position Verification ===");
        var allActors = actorsByClass
            .SelectMany(kv => kv.Value.Select(a => (Class: kv.Key, Actor: (JObject)a)))
            .ToList();

        // 1. Compare BossSpawnerLoactionData rows against nearest matching spawner actor (and by SpawnerName if present).
        try
        {
            var bossRows = GameTables.LoadRows(provider, BossLocationTable);
            var spawnerActors = allActors
                .Where(a => a.Class.Contains("Spawner", StringComparison.OrdinalIgnoreCase)
                            && a.Class.Contains("Pal", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var checkedBoss = 0;
            foreach (var row in bossRows.Properties())
            {
                if (checkedBoss >= 5) break;
                var p = (JObject)row.Value;
                var spawnerId = p["SpawnerID"]?.Value<string>();
                var loc = p["Location"];
                if (spawnerId == null || loc == null) continue;
                double bx = loc["X"]!.Value<double>(), by = loc["Y"]!.Value<double>(), bz = loc["Z"]!.Value<double>();

                var byName = spawnerActors.FirstOrDefault(a =>
                    string.Equals(a.Actor["props"]?["SpawnerName"]?.Value<string>(), spawnerId, StringComparison.OrdinalIgnoreCase));
                var nearest = spawnerActors
                    .Select(a => (a, Dist: Dist2D(bx, by, a.Actor["x"]!.Value<double>(), a.Actor["y"]!.Value<double>())))
                    .OrderBy(x => x.Dist)
                    .FirstOrDefault();

                if (byName.Actor != null)
                {
                    var d = Dist2D(bx, by, byName.Actor["x"]!.Value<double>(), byName.Actor["y"]!.Value<double>());
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  Boss '{spawnerId}' ({p["CharacterID"]}): table=({bx:F1}, {by:F1}) vs actor[{byName.Class}]=({byName.Actor["x"]?.Value<double>():F1}, {byName.Actor["y"]?.Value<double>():F1}) delta2D={d:F1}"));
                    checkedBoss++;
                }
                else if (nearest.a.Actor != null)
                {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  Boss '{spawnerId}' ({p["CharacterID"]}): table=({bx:F1}, {by:F1}) vs nearest[{nearest.a.Class}]=({nearest.a.Actor["x"]?.Value<double>():F1}, {nearest.a.Actor["y"]?.Value<double>():F1}) delta2D={nearest.Dist:F1}"));
                    checkedBoss++;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  Boss verification failed: " + ex.Message);
        }

        // 2. Compare DT_PalQuestLocationData Main_UnlockFastTravel against nearest BP_LevelObject_TowerFastTravelPoint.
        try
        {
            var questRows = GameTables.LoadRows(provider, QuestLocationTable);
            var ftQuest = questRows["Main_UnlockFastTravel"];
            if (ftQuest?["Position"] is JObject pos)
            {
                double qx = pos["X"]!.Value<double>(), qy = pos["Y"]!.Value<double>();
                var ftActors = allActors
                    .Where(a => a.Class.Contains("FastTravel", StringComparison.OrdinalIgnoreCase))
                    .Select(a => (a, Dist: Dist2D(qx, qy, a.Actor["x"]!.Value<double>(), a.Actor["y"]!.Value<double>())))
                    .OrderBy(x => x.Dist)
                    .Take(3)
                    .ToList();
                foreach (var (a, dist) in ftActors)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  Quest Main_UnlockFastTravel: table=({qx:F1}, {qy:F1}) vs {a.Class}=({a.Actor["x"]?.Value<double>():F1}, {a.Actor["y"]?.Value<double>():F1}) delta2D={dist:F1}"));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FastTravel verification failed: " + ex.Message);
        }
    }

    private static double Dist2D(double x1, double y1, double x2, double y2) =>
        Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
}
