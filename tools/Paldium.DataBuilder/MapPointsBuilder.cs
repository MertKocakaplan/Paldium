using System.Globalization;
using System.Text.Json;
using Paldium.Core.Data.Schema;
using Paldium.Core.Domain;
using Paldium.Core.Map;

namespace Paldium.DataBuilder;

/// <summary>
/// Wave 2: Builds <c>data/map/map-points.json</c> strictly from game files:
/// <c>world-actors.json</c>, <c>map-tables.json</c>, <c>texts.json</c>, <c>world-map.json</c>, <c>tree-map.json</c>, <c>quests.json</c>,
/// <c>pickups.json</c> and <c>gamedata.json</c>.
/// Coordinates are rounded to 1 decimal place and bulk points omit redundant names so the file stays well under 4 MB.
/// </summary>
public static class MapPointsBuilder
{
    /// <summary>
    /// Actors below this height are inside the dungeon level instances, which lie under the map: their world position says nothing
    /// about a place on the map. (The surface goes down to about -2,100; the dungeon rooms start below -36,000.)
    /// </summary>
    public const double UndergroundZ = -20000;

    /// <summary>
    /// The streaming cells of the data layer "dungeon": 13 portal markers around the start island that neither the game's own
    /// 157 dungeon entrances elsewhere nor a public map shows. Left out until they are seen in the game (T138 entry condition).
    /// </summary>
    private const string UnconfirmedDungeonLayer = "_DL18484F9F";

    /// <summary>
    /// Ore spawners of the persistent level (outside every data layer) lie in biomes without that resource; the layered ones
    /// match the public map counts exactly. They are left out of the material categories.
    /// </summary>
    private const string PersistentLevelCell = "PL_MainWorld5";

    /// <summary>
    /// Categories whose places have not been checked on a public map or in the game yet (T138 entry condition 3): they are not
    /// written. Remove an id once three of its points are confirmed (REPORT lists them) and run the builder again.
    /// </summary>
    public static readonly IReadOnlySet<string> AwaitingCheck = new HashSet<string>(StringComparer.Ordinal)
    {
        "fishing", "supply", "arena", "respawn", "treasure_map", "enemy_camp", "night_sand",
    };

    public static MapDataFile Build(string extractorDir, string worldDir, GameDataFile gameData)
    {
        var actorsPath = File.Exists(worldDir) ? worldDir : Path.Combine(worldDir, "world-actors.json");
        using var worldDoc = JsonDocument.Parse(File.ReadAllText(actorsPath));
        using var tablesDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(extractorDir, "map-tables.json")));
        using var textsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(extractorDir, "texts.json")));

        var actorsByClass = new Dictionary<string, List<JsonElement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in worldDoc.RootElement.EnumerateObject())
        {
            var surface = prop.Value.EnumerateArray()
                .Where(a => !a.TryGetProperty("z", out var z) || z.GetDouble() >= UndergroundZ)
                .ToList();
            if (surface.Count > 0) actorsByClass[prop.Name] = surface;
        }

        var tables = tablesDoc.RootElement;
        var texts = textsDoc.RootElement;
        var palById = gameData.Pals.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        // The two pictures overlap a little: the main map wins there, like MapTransform.Locate.
        static string? MapKey(double x, double y) =>
            MapProjection.Main.Contains(x, y) ? "MainMap" : MapProjection.Tree.Contains(x, y) ? "Tree" : null;

        static (double X, double Y, int? Mx, int? My, string Map) LocatePoint(double x, double y)
        {
            var m = MapKey(x, y) ?? "MainMap";
            var rx = Math.Round(x, 1);
            var ry = Math.Round(y, 1);
            if (m == "MainMap")
            {
                var mc = MapProjection.Main.ToMapCoordinates(rx, ry)!.Value;
                return (rx, ry, (int)Math.Round(mc.MapX), (int)Math.Round(mc.MapY), "MainMap");
            }
            return (rx, ry, null, null, "Tree");
        }

        static string? Str(JsonElement el, string name) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        SortedDictionary<string, string> TextsObj(JsonElement el)
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (el.ValueKind != JsonValueKind.Object) return d;
            foreach (var p in el.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                    d[p.Name] = p.Value.GetString()!;
            return d;
        }

        string? LookupText(string table, string key, string lang)
        {
            if (texts.TryGetProperty(lang, out var langObj) &&
                langObj.TryGetProperty(table, out var tbl) &&
                tbl.TryGetProperty(key, out var val) &&
                val.ValueKind == JsonValueKind.String)
            {
                var s = val.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s) && s is not ("-" or "???") && !s.EndsWith(" Text", StringComparison.Ordinal))
                    return s;
            }
            return null;
        }

        SortedDictionary<string, string> Bilingual(string table, string key, string? fallbackEn = null, string? fallbackTr = null)
        {
            var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (LookupText(table, key, "en") is { } en) d["en"] = en;
            else if (!string.IsNullOrEmpty(fallbackEn)) d["en"] = fallbackEn;
            if (LookupText(table, key, "tr") is { } tr) d["tr"] = tr;
            else if (!string.IsNullOrEmpty(fallbackTr)) d["tr"] = fallbackTr;
            else if (d.TryGetValue("en", out var e)) d["tr"] = e;
            return d;
        }

        // 1. Maps
        var maps = new List<MapBoundsDto>
        {
            new("MainMap", "world-map.png", "world-map-large.png",
                MapProjection.Main.MinX, MapProjection.Main.MaxX, MapProjection.Main.MinY, MapProjection.Main.MaxY, true),
            new("Tree", "tree-map.png", "tree-map-large.png",
                MapProjection.Tree.MinX, MapProjection.Tree.MaxX, MapProjection.Tree.MinY, MapProjection.Tree.MaxY, false),
        };

        // 2. Regions (DT_WorldMapAreaData + triggers + wide regions + WorldTree regions)
        var areaTable = tables.GetProperty("areas");
        var wideRegions = tables.GetProperty("wideRegions");
        var regions = new List<MapRegionDto>();
        var regionTriggers = new Dictionary<string, (double X, double Y, double? Hx, double? Hy, double? Radius)>(StringComparer.OrdinalIgnoreCase);

        void ReadTriggers(string clsName, bool isSphere)
        {
            if (!actorsByClass.TryGetValue(clsName, out var list)) return;
            foreach (var a in list)
            {
                if (!a.TryGetProperty("props", out var pr) || !pr.TryGetProperty("AreaName", out var an) ||
                    !an.TryGetProperty("Key", out var kv)) continue;
                var key = kv.GetString();
                if (string.IsNullOrEmpty(key) || regionTriggers.ContainsKey(key)) continue;
                double x = a.GetProperty("x").GetDouble(), y = a.GetProperty("y").GetDouble();
                double? hx = null, hy = null, rad = null;
                if (!isSphere && pr.TryGetProperty("BoxExtent", out var be))
                {
                    hx = Math.Round(be.GetProperty("X").GetDouble(), 1);
                    hy = Math.Round(be.GetProperty("Y").GetDouble(), 1);
                }
                if (isSphere && pr.TryGetProperty("SphereRadius", out var sr))
                    rad = Math.Round(sr.GetDouble(), 1);
                regionTriggers[key] = (x, y, hx, hy, rad);
            }
        }
        ReadTriggers("BP_PalRegionTriggerBox_C", false);
        ReadTriggers("BP_PalRegionTriggerSphere_C", true);

        foreach (var areaProp in areaTable.EnumerateObject())
        {
            if (!regionTriggers.TryGetValue(areaProp.Name, out var trig)) continue;
            var names = TextsObj(areaProp.Value.GetProperty("names"));
            if (names.Count == 0) continue;
            var loc = LocatePoint(trig.X, trig.Y);
            // Wide islands vs subregions vs towers/caves/preserves
            var level = areaProp.Name.Contains("Boss", StringComparison.OrdinalIgnoreCase) ||
                        areaProp.Name.Contains("Cave", StringComparison.OrdinalIgnoreCase) ||
                        areaProp.Name.Contains("SmallIsland", StringComparison.OrdinalIgnoreCase) ||
                        areaProp.Name.StartsWith("Preserve_", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 1;
            regions.Add(new MapRegionDto(areaProp.Name, loc.Map, level, names, loc.X, loc.Y, loc.Mx, loc.My, trig.Hx, trig.Hy, trig.Radius));
        }

        // Level 0 wide regions from region trigger centroids
        void AddWideFromTriggers(string wideKey, Func<string, bool> match)
        {
            if (!wideRegions.TryGetProperty(wideKey, out var nEl)) return;
            var matched = regions.Where(r => r.Map == "MainMap" && match(r.Id)).ToList();
            if (matched.Count == 0) return;
            double cx = matched.Average(r => r.X), cy = matched.Average(r => r.Y);
            var loc = LocatePoint(cx, cy);
            regions.Add(new MapRegionDto(wideKey, "MainMap", 0, TextsObj(nEl), loc.X, loc.Y, loc.Mx, loc.My));
        }
        AddWideFromTriggers("REGION_Wide_Sakurajima01", id => id.StartsWith("Sakurajima", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Sakurajim_", StringComparison.OrdinalIgnoreCase));
        AddWideFromTriggers("REGION_Wide_Darkisland01", id => id.StartsWith("Darkisland", StringComparison.OrdinalIgnoreCase));
        AddWideFromTriggers("REGION_Wide_Skyisland01", id => id.StartsWith("Skyisland", StringComparison.OrdinalIgnoreCase));

        // Add major mainland biome Level 0 centroids (Grass / Forest / Desert / Frost / Volcano) for high-level map zoom
        foreach (var (wideKey, areaId) in new[]
        {
            ("REGION_Grass_1", "Grass_001"),
            ("REGION_Forest_1", "Forest_001"),
            ("REGION_Desert_1", "Desert_001"),
            ("REGION_Frost_1", "Frost_001"),
            ("REGION_Volcano_1", "Volcano_001"),
        })
        {
            if (regionTriggers.TryGetValue(areaId, out var trig))
            {
                var names = Bilingual("worldMap", wideKey);
                var loc = LocatePoint(trig.X, trig.Y);
                if (names.Count > 0)
                    regions.Add(new MapRegionDto(wideKey, "MainMap", 0, names, loc.X, loc.Y, loc.Mx, loc.My));
            }
        }

        // World Tree Level 0 + Level 1 regions anchored to World Tree Fast Travel points (approximate area centers from game FT points)
        var ftActors = actorsByClass.TryGetValue("BP_LevelObject_TowerFastTravelPoint_C", out var ftList) ? ftList : [];
        var ftById = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ft in ftActors)
        {
            var fid = ft.TryGetProperty("props", out var pr) && pr.TryGetProperty("FastTravelPointID", out var idEl) ? idEl.GetString() : null;
            if (!string.IsNullOrEmpty(fid))
                ftById[fid] = (ft.GetProperty("x").GetDouble(), ft.GetProperty("y").GetDouble());
        }

        var treeWideNames = gameData.Terms != null && gameData.Terms.TryGetValue("Map.Tree", out var mtNames)
            ? new SortedDictionary<string, string>(mtNames, StringComparer.Ordinal)
            : Bilingual("worldMap", "WORLDMAP_NAME_Tree");
        if (ftById.Values.Where(v => MapProjection.Tree.Contains(v.X, v.Y)).ToList() is { Count: > 0 } treeFts)
        {
            regions.Add(new MapRegionDto(
                "WORLDMAP_NAME_Tree", "Tree", 0, treeWideNames,
                Math.Round(treeFts.Average(v => v.X), 1),
                Math.Round(treeFts.Average(v => v.Y), 1)));
        }

        // Approximate centers of REGION_WorldTree01..12 linked from identical WorldTree FT names in DT_MapRespawnPointInfoText
        var treeRegionToFt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["REGION_WorldTree01"] = "WorldTree_A",
            ["REGION_WorldTree02"] = "WorldTree_B",
            ["REGION_WorldTree03"] = "WorldTree_C_1",
            ["REGION_WorldTree04"] = "WorldTree_C_2",
            ["REGION_WorldTree05"] = "WorldTree_D",
            ["REGION_WorldTree06"] = "WorldTree_E",
            ["REGION_WorldTree07"] = "WorldTree_I",
            ["REGION_WorldTree08"] = "WorldTree_LastBoss",
            ["REGION_WorldTree09"] = "WorldTree_lab",
            ["REGION_WorldTree10"] = "WorldTree_L",
            ["REGION_WorldTree11"] = "WorldTree_M",
            ["REGION_WorldTree12"] = "WorldTree_N",
        };
        foreach (var (regKey, ftId) in treeRegionToFt)
        {
            if (wideRegions.TryGetProperty(regKey, out var nEl) && ftById.TryGetValue(ftId, out var pos))
                regions.Add(new MapRegionDto(regKey, "Tree", 1, TextsObj(nEl), Math.Round(pos.X, 1), Math.Round(pos.Y, 1)));
        }

        string? NearestRegion(string mapId, double x, double y)
        {
            string? best = null;
            double bestScore = double.MaxValue;
            foreach (var r in regions)
            {
                if (!string.Equals(r.Map, mapId, StringComparison.OrdinalIgnoreCase) || r.Level == 0) continue;
                var d2 = (r.X - x) * (r.X - x) + (r.Y - y) * (r.Y - y);
                if (d2 < bestScore) { bestScore = d2; best = r.Id; }
            }
            return best;
        }

        // 3. Categories. Names come from the game's own texts; a category the game has no text for gets no name here
        // and the app uses its Map.Cat.<id> resource.
        SortedDictionary<string, string> GameName(string table, string key) => Bilingual(table, key);
        SortedDictionary<string, string> NoName() => new(StringComparer.Ordinal);
        MapCategoryDto Material(string id, string itemId) =>
            new(id, "Materials", GameName("itemName", "ITEM_NAME_" + itemId), "icons/items/" + itemId + ".png", false);

        var categories = new List<MapCategoryDto>
        {
            // Locations
            new("fast_travel", "Locations", GameName("uiCommon", "MAP_FILTER_FT"), "icons/map/fast_travel.png", true),
            new("boss_tower", "Locations", GameName("uiCommon", "MAP_FILTER_BOSSTOWER"), "icons/map/boss_tower.png", true),
            new("watchtower", "Locations", NoName(), "icons/map/watchtower.png", true),
            new("sealed_realm", "Locations", NoName(), "icons/map/sealed_realm.png", true),
            new("dungeon", "Locations", NoName(), "icons/map/dungeon.png", true),
            new("oil_rig", "Locations", GameName("uiCommon", "MAP_FILTER_OILRIG"), "icons/map/oil_rig.png", false),
            new("arena", "Locations", GameName("worldMap", "REGION_Arena"), "icons/map/arena.png", false),
            new("respawn", "Locations", NoName(), "icons/map/respawn.png", false),
            new("fishing", "Locations", NoName(), null, false),
            new("supply", "Locations", NoName(), "icons/map/supply.png", false),
            // NPC
            new("merchant", "Npc", NoName(), null, true),
            new("npc", "Npc", NoName(), null, false),
            new("quest_giver", "Npc", NoName(), "icons/map/quest_giver.png", true),
            // Collectibles
            new("effigy", "Collectibles", NoName(), "icons/map/effigy.png", false),
            new("pickup_tower", "Collectibles", NoName(), "icons/map/pickup_tower.png", true),
            new("journal", "Collectibles", NoName(), null, false),
            new("dog_coin", "Collectibles", GameName("itemName", "ITEM_NAME_DogCoin"), "icons/items/DogCoin.png", false),
            new("treasure_map", "Collectibles", GameName("uiCommon", "MAP_FILTER_TREASUREMAPPOINT"), "icons/map/treasure_map.png", false),
            // Items
            new("chest", "Items", NoName(), null, false),
            new("egg", "Items", GameName("itemName", "ITEM_NAME_PalEgg"), null, false),
            new("skill_fruit", "Items", NoName(), null, false),
            // Enemies
            new("alpha", "Enemies", GameName("uiCommon", "MAP_FILTER_BOSS"), "icons/map/alpha.png", true),
            // The predators have no places of their own: the app draws the wild areas of their base species (T149).
            new("predator", "Enemies", NoName(), "icons/map/alpha.png", false),
            new("bounty", "Enemies", GameName("uiCommon", "MAP_FILTER_HUMANBOSS"), "icons/map/bounty.png", false),
            new("enemy_camp", "Enemies", NoName(), "icons/map/enemy_camp.png", false),
            // Quests
            new("quest", "Quests", NoName(), "icons/map/quest.png", false),
            // Materials
            Material("ore", "CopperOre"),
            Material("coal", "Coal"),
            Material("sulfur", "Sulfur"),
            Material("quartz", "Quartz"),
            Material("paldium", "Pal_crystal_S"),
            Material("oil", "CrudeOil"),
            Material("hexolite", "RainbowCrystal"),
            Material("night_sand", "NightStone"),
            Material("sky_ore", "SkyIslandOre"),
            Material("worldtree_ore", "WorldTreeOre"),
        };

        var points = new List<MapMarkerDto>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        string MakeId(string preferred, double x, double y, string cat)
        {
            var baseId = string.IsNullOrWhiteSpace(preferred)
                ? string.Create(CultureInfo.InvariantCulture, $"{cat}:{Math.Round(x, 0)}_{Math.Round(y, 0)}")
                : preferred;
            if (usedIds.Add(baseId)) return baseId;
            for (var n = 2; ; n++)
            {
                var candidate = $"{baseId}#{n}";
                if (usedIds.Add(candidate)) return candidate;
            }
        }

        string? AddPoint(string preferredId, string cat, double x, double y,
            SortedDictionary<string, string>? names = null, string? rk = null, string? ri = null, string? ex = null,
            IReadOnlyList<string>? quests = null, IReadOnlyList<MapStackDto>? items = null, SortedDictionary<string, string>? hint = null,
            SortedDictionary<string, string>? boss = null)
        {
            if (MapKey(x, y) == null) return null;
            var loc = LocatePoint(x, y);
            var id = MakeId(preferredId, loc.X, loc.Y, cat);
            points.Add(new MapMarkerDto(
                id, cat, loc.Map, loc.X, loc.Y, loc.Mx, loc.My,
                names is { Count: > 0 } ? names : null,
                NearestRegion(loc.Map, loc.X, loc.Y),
                rk, ri, ex, quests, items is { Count: > 0 } ? items : null, hint is { Count: > 0 } ? hint : null,
                boss is { Count: > 0 } ? boss : null));
            return id;
        }

        // Fast Travel (152)
        var ftNamesTable = tables.GetProperty("fastTravelNames");
        foreach (var ft in ftActors)
        {
            var fid = ft.TryGetProperty("props", out var pr) && pr.TryGetProperty("FastTravelPointID", out var idEl) ? idEl.GetString() : null;
            var iid = ft.TryGetProperty("props", out pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
            SortedDictionary<string, string>? n = null;
            if (!string.IsNullOrEmpty(fid) && ftNamesTable.TryGetProperty(fid, out var ftNm)) n = TextsObj(ftNm);
            AddPoint(iid ?? $"ft:{fid}", "fast_travel", ft.GetProperty("x").GetDouble(), ft.GetProperty("y").GetDouble(), n);
        }

        // Respawn points (the game has only a description for them, no name)
        if (actorsByClass.TryGetValue("BP_LevelObject_StaticRespawnPoint_C", out var rspList))
        {
            foreach (var r in rspList)
            {
                var rid = r.TryGetProperty("props", out var pr) && pr.TryGetProperty("RespawnPointID", out var idEl) ? idEl.GetString() : null;
                var iid = r.TryGetProperty("props", out pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
                AddPoint(iid ?? $"rsp:{rid}", "respawn", r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble());
            }
        }

        // Watchtowers (T148): the places that uncover the map and work as fast travel points.
        if (actorsByClass.TryGetValue("BP_LevelObject_UnlockMapPoint_C", out var watchList))
        {
            foreach (var tower in watchList)
            {
                var props = tower.GetProperty("props");
                var fid = Str(props, "FastTravelPointID");
                var name = !string.IsNullOrEmpty(fid) && ftNamesTable.TryGetProperty(fid, out var named) ? TextsObj(named) : null;
                AddPoint(Str(props, "LevelObjectInstanceId") ?? $"watch:{fid}", "watchtower",
                    tower.GetProperty("x").GetDouble(), tower.GetProperty("y").GetDouble(), name);
            }
        }

        // Boss Towers (13)
        var bossTypeToWorldMapKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GrassBoss"] = "REGION_Grass_Boss",
            ["ForestBoss"] = "REGION_Forest_Boss",
            ["ElectricBoss"] = "REGION_Volcano_Boss",
            ["DesertBoss"] = "REGION_Desert_Boss",
            ["SnowBoss"] = "REGION_Frost_Boss",
            ["SakurajimaBoss"] = "REGION_Sakurajima_Boss",
            ["VikingBoss"] = "REGION_Darkisland_Boss",
            ["SorajimaBoss"] = "REGION_Skyisland_Boss",
            ["WorldTreeBoss"] = "REGION_WorldTree08",
        };
        var towerBosses = tables.GetProperty("towerBosses");
        foreach (var cls in actorsByClass.Keys.Where(c => c.StartsWith("BP_PalBossTower", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var tw in actorsByClass[cls])
            {
                var bType = tw.TryGetProperty("props", out var pr) && pr.TryGetProperty("BossType", out var bt) ? bt.GetString() : null;
                var shortType = bType?.Split("::").LastOrDefault();
                if (cls.Contains("KingWhale", StringComparison.OrdinalIgnoreCase)) shortType = "KingWhale";
                SortedDictionary<string, string>? n = null;
                if (shortType != null && bossTypeToWorldMapKey.TryGetValue(shortType, out var wmKey))
                    n = Bilingual("worldMap", wmKey);
                else if (shortType == "KingWhale")
                    n = Bilingual("worldMap", "REGION_Boss_KingWhale");
                else if (shortType is "WorldTreeMiddleBoss1" or "WorldTreeMiddleBoss2" or "WorldTreeMiddleBoss3")
                {
                    var ftKey = "WorldTree_MiddleBoss_" + shortType[^1];
                    if (ftNamesTable.TryGetProperty(ftKey, out var ftNm)) n = TextsObj(ftNm);
                }
                // T148: the game's tower name, the boss and the key a save lists the defeated tower under.
                var type = shortType == "KingWhale" ? "KingWhaleBoss" : shortType;
                SortedDictionary<string, string>? bossName = null;
                string? defeatKey = null;
                if (type != null && towerBosses.TryGetProperty(type, out var tower))
                {
                    defeatKey = tower.GetProperty("key").GetString();
                    if (TextsObj(tower.GetProperty("names")) is { Count: > 0 } official) n = official;
                    bossName = TextsObj(tower.GetProperty("boss"));
                }
                AddPoint($"tower:{shortType}", "boss_tower", tw.GetProperty("x").GetDouble(), tw.GetProperty("y").GetDouble(), n,
                    rk: defeatKey != null ? "tower" : null, ri: defeatKey, boss: bossName);
            }
        }

        // Sealed Realms (18). Their bosses are rows of the boss location table too: shown on the entrance, not again as an Alpha.
        var bossRows = tables.GetProperty("bosses").EnumerateArray().ToList();
        var bossBySpawner = bossRows.GroupBy(b => b.GetProperty("spawnerId").GetString()!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sealedSpawners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? PalOf(string characterId) => gameData.Pals.FirstOrDefault(p =>
            string.Equals(p.Id, characterId.StartsWith("BOSS_", StringComparison.OrdinalIgnoreCase) ? characterId[5..] : characterId, StringComparison.OrdinalIgnoreCase))?.Id;
        foreach (var cls in actorsByClass.Keys.Where(c => c.StartsWith("BP_DungeonFixedEntrance_", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var sr in actorsByClass[cls])
            {
                var pr = sr.TryGetProperty("props", out var p) ? p : default;
                var iid = pr.ValueKind == JsonValueKind.Object && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
                var dnKey = pr.ValueKind == JsonValueKind.Object && pr.TryGetProperty("DungeonNameRowHandle", out var dh) && dh.TryGetProperty("RowName", out var rn)
                    ? rn.GetString()
                    : null;
                var n = !string.IsNullOrEmpty(dnKey) ? Bilingual("dungeonName", dnKey) : null;
                string? bossPal = null;
                if (pr.ValueKind == JsonValueKind.Object && pr.TryGetProperty("EnemySpawnerSoftClass", out var spSoft) &&
                    spSoft.TryGetProperty("AssetPathName", out var ap) && ap.GetString() is { } assetStr)
                {
                    var spName = assetStr.Split('.').LastOrDefault()?.Replace("BP_PalSpawner_Sheets_", "", StringComparison.OrdinalIgnoreCase);
                    if (spName != null && spName.EndsWith("_C", StringComparison.OrdinalIgnoreCase)) spName = spName[..^2];
                    if (!string.IsNullOrEmpty(spName)) bossPal = spName;
                }
                string? palId = null, level = null;
                if (bossPal != null && bossBySpawner.TryGetValue(bossPal, out var boss))
                {
                    sealedSpawners.Add(bossPal);
                    palId = PalOf(boss.GetProperty("characterId").GetString()!);
                    level = $"Lv {boss.GetProperty("level").GetInt32()}";
                }
                AddPoint(iid ?? $"sealed:{cls}", "sealed_realm", sr.GetProperty("x").GetDouble(), sr.GetProperty("y").GetDouble(), n,
                    rk: palId != null ? "pal" : null, ri: palId, ex: level);
            }
        }

        // Dungeons (170)
        foreach (var cls in actorsByClass.Keys.Where(c => c.StartsWith("BP_DungeonPortalMarker_", StringComparison.OrdinalIgnoreCase)))
        {
            var biomeTag = cls["BP_DungeonPortalMarker_".Length..^2].Replace("_C", "", StringComparison.Ordinal);
            foreach (var d in actorsByClass[cls])
            {
                if (d.GetProperty("cell").GetString()!.EndsWith(UnconfirmedDungeonLayer, StringComparison.Ordinal)) continue;
                var iid = d.TryGetProperty("props", out var pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
                AddPoint(iid ?? "", "dungeon", d.GetProperty("x").GetDouble(), d.GetProperty("y").GetDouble(), ex: biomeTag);
            }
        }

        // Oil Rigs (3)
        foreach (var rig in tables.GetProperty("oilRigs").EnumerateArray())
        {
            var id = rig.GetProperty("id").GetString()!;
            var lv = rig.GetProperty("level").GetInt32();
            AddPoint(id, "oil_rig", rig.GetProperty("x").GetDouble(), rig.GetProperty("y").GetDouble(),
                TextsObj(rig.GetProperty("names")), ex: $"Lv {lv}");
        }

        // Arena (1)
        if (actorsByClass.TryGetValue("BP_ArenaEntrance_C", out var arenaList))
        {
            foreach (var ar in arenaList)
            {
                var n = Bilingual("worldMap", "REGION_Arena");
                AddPoint("arena:entrance", "arena", ar.GetProperty("x").GetDouble(), ar.GetProperty("y").GetDouble(), n);
            }
        }

        // Fishing Spots (overworld) & Supply Drops
        foreach (var cls in actorsByClass.Keys.Where(c => c.StartsWith("BP_FishingSpot_", StringComparison.OrdinalIgnoreCase)))
        {
            var tag = cls["BP_FishingSpot_".Length..^2];
            foreach (var fs in actorsByClass[cls])
            {
                var iid = fs.TryGetProperty("props", out var pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
                AddPoint(iid ?? "", "fishing", fs.GetProperty("x").GetDouble(), fs.GetProperty("y").GetDouble(), ex: tag);
            }
        }
        foreach (var cls in actorsByClass.Keys.Where(c => c.StartsWith("BP_SupplySpawner_", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var sp in actorsByClass[cls])
                AddPoint("", "supply", sp.GetProperty("x").GetDouble(), sp.GetProperty("y").GetDouble());
        }

        // Merchants (overworld placed shops from gameData.Shops)
        var merchantCoords = new List<(double X, double Y)>();
        if (gameData.Shops != null)
        {
            foreach (var shop in gameData.Shops)
            {
                foreach (var mp in shop.Place.MapPoints)
                {
                    merchantCoords.Add((mp.X, mp.Y));
                    var n = new SortedDictionary<string, string>(shop.VendorNames, StringComparer.Ordinal);
                    AddPoint(ShopMapIds.PointId(shop.Id, mp.X, mp.Y), "merchant", mp.X, mp.Y,
                        n, rk: "shop", ri: shop.Id);
                }
            }
        }

        // Unique / Navigator NPCs (overworld, not duplicating placed shops)
        foreach (var cls in new[] { "BP_MonoNPCSpawner_C", "BP_MonoNPCSpawner_Unique_C" })
        {
            if (!actorsByClass.TryGetValue(cls, out var npcList)) continue;
            foreach (var npc in npcList)
            {
                if (!npc.TryGetProperty("props", out var pr)) continue;
                var un = pr.TryGetProperty("UniqueName", out var unEl) && unEl.TryGetProperty("Key", out var uk) ? uk.GetString() : null;
                if (string.IsNullOrEmpty(un)) continue;

                double nx = npc.GetProperty("x").GetDouble(), ny = npc.GetProperty("y").GetDouble();
                if (merchantCoords.Any(m => Math.Abs(m.X - nx) < 10 && Math.Abs(m.Y - ny) < 10)) continue;

                var lookupKey = un.StartsWith("BountyNavigator", StringComparison.OrdinalIgnoreCase) ? "BountyNavigator" : un;
                var names = Bilingual("uniqueNpcText", lookupKey, LookupText("humanName", "NAME_" + lookupKey, "en"), LookupText("humanName", "NAME_" + lookupKey, "tr"));
                if (names.Count == 0) names = Bilingual("uniqueNpcText", un.TrimStart('U', '_'));
                if (names.Count == 0) continue; // skip nameless generic fillers
                AddPoint($"npc:{un}", "npc", nx, ny, names);
            }
        }

        // Effigies with their LevelObjectInstanceId (T138d match) and their type
        var relicClasses = tables.GetProperty("relicClasses");
        foreach (var cls in actorsByClass.Keys.Where(c => c.StartsWith("BP_LevelObject_Relic", StringComparison.OrdinalIgnoreCase)))
        {
            var type = relicClasses.TryGetProperty(cls, out var typeEl) ? typeEl.GetString() : null;
            foreach (var rel in actorsByClass[cls])
            {
                var iid = rel.TryGetProperty("props", out var pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
                AddPoint(iid ?? "", "effigy", rel.GetProperty("x").GetDouble(), rel.GetProperty("y").GetDouble(), ex: type);
            }
        }

        // Journals (64)
        var noteTable = tables.GetProperty("notes");
        if (actorsByClass.TryGetValue("BP_LevelObject_Note_C", out var noteList))
        {
            foreach (var nt in noteList)
            {
                var iid = nt.TryGetProperty("props", out var pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
                var nr = nt.TryGetProperty("props", out pr) && pr.TryGetProperty("NoteRowName", out var nk) && nk.TryGetProperty("Key", out var nkv) ? nkv.GetString() : null;
                var n = !string.IsNullOrEmpty(nr) && noteTable.TryGetProperty(nr, out var nObj) ? TextsObj(nObj.GetProperty("names")) : null;
                // A save lists a read journal under its row name (T144).
                AddPoint(iid ?? $"note:{nr}", "journal", nt.GetProperty("x").GetDouble(), nt.GetProperty("y").GetDouble(), n, ex: nr);
            }
        }

        // Dog Coins & Treasure Map Spots
        if (actorsByClass.TryGetValue("BP_PalMapObjectSpawner_DogCoin_C", out var dcList))
            foreach (var d in dcList)
                AddPoint(d.TryGetProperty("props", out var pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() ?? "" : "",
                    "dog_coin", d.GetProperty("x").GetDouble(), d.GetProperty("y").GetDouble());

        if (actorsByClass.TryGetValue("BP_LevelObject_TreasureMapPoint_C", out var tmList))
            foreach (var tm in tmList)
                AddPoint(tm.TryGetProperty("props", out var pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() ?? "" : "",
                    "treasure_map", tm.GetProperty("x").GetDouble(), tm.GetProperty("y").GetDouble());

        // Chests, Eggs, Skill Fruits
        foreach (var cls in actorsByClass.Keys.Where(c =>
                     ((c.Contains("Treasure_", StringComparison.OrdinalIgnoreCase) &&
                       !c.Contains("VisibleContent", StringComparison.OrdinalIgnoreCase) &&
                       !c.Contains("Visual", StringComparison.OrdinalIgnoreCase)) ||
                      c.Equals("BP_OilrigTreasureBoxSpawner_C", StringComparison.OrdinalIgnoreCase) ||
                      c.Equals("BP_OilrigTreasureBoxSpawner_Goal_C", StringComparison.OrdinalIgnoreCase) ||
                      c.Equals("BP_OilrigGoalCrateSpawner_C", StringComparison.OrdinalIgnoreCase))))
        {
            var tag = cls.Replace("BP_PalMapObjectSpawner_Treasure_", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("BP_OilrigTreasureBoxSpawner_Goal_C", "OilrigGoal", StringComparison.OrdinalIgnoreCase)
                         .Replace("BP_OilrigTreasureBoxSpawner_C", "Oilrig", StringComparison.OrdinalIgnoreCase)
                         .Replace("BP_OilrigGoalCrateSpawner_C", "OilrigGoal", StringComparison.OrdinalIgnoreCase)
                         .TrimEnd('_');
            if (tag.EndsWith("_C", StringComparison.OrdinalIgnoreCase)) tag = tag[..^2];
            foreach (var ch in actorsByClass[cls])
            {
                var iid = ch.TryGetProperty("props", out var pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
                AddPoint(iid ?? "", "chest", ch.GetProperty("x").GetDouble(), ch.GetProperty("y").GetDouble(), ex: tag);
            }
        }

        foreach (var cls in actorsByClass.Keys.Where(c => c.StartsWith("bp_palmapobjectspawner_palegg_", StringComparison.OrdinalIgnoreCase)))
        {
            var tag = cls["bp_palmapobjectspawner_palegg_".Length..^2];
            foreach (var eg in actorsByClass[cls])
            {
                var iid = eg.TryGetProperty("props", out var pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
                AddPoint(iid ?? "", "egg", eg.GetProperty("x").GetDouble(), eg.GetProperty("y").GetDouble(), ex: tag);
            }
        }

        foreach (var cls in actorsByClass.Keys.Where(c => c.StartsWith("BP_PalMapObjectSpawner_SkillFruits_", StringComparison.OrdinalIgnoreCase)))
        {
            var tag = cls["BP_PalMapObjectSpawner_SkillFruits_".Length..^2];
            foreach (var sf in actorsByClass[cls])
            {
                var iid = sf.TryGetProperty("props", out var pr) && pr.TryGetProperty("LevelObjectInstanceId", out var iEl) ? iEl.GetString() : null;
                AddPoint(iid ?? "", "skill_fruit", sf.GetProperty("x").GetDouble(), sf.GetProperty("y").GetDouble(), ex: tag);
            }
        }

        // Alphas (90), Bounties (33), Enemy Camps (56)
        foreach (var b in bossRows)
        {
            var spawnerId = b.GetProperty("spawnerId").GetString()!;
            if (sealedSpawners.Contains(spawnerId)) continue;
            var normId = PalOf(b.GetProperty("characterId").GetString()!);
            var names = normId != null && palById.TryGetValue(normId, out var pal) ? new SortedDictionary<string, string>(pal.Names, StringComparer.Ordinal) : null;
            var lv = b.GetProperty("level").GetInt32();
            AddPoint($"alpha:{spawnerId}", "alpha", b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(),
                names, rk: normId != null ? "pal" : null, ri: normId, ex: $"Lv {lv}");
        }

        foreach (var bo in tables.GetProperty("bounties").EnumerateArray())
        {
            var id = bo.GetProperty("id").GetString()!;
            var lv = bo.GetProperty("level").GetInt32();
            AddPoint($"bounty:{id}", "bounty", bo.GetProperty("x").GetDouble(), bo.GetProperty("y").GetDouble(),
                TextsObj(bo.GetProperty("names")), ex: $"Lv {lv}");
        }

        foreach (var cls in actorsByClass.Keys.Where(c =>
                     c.StartsWith("BP_NPCCampSpawner_", StringComparison.OrdinalIgnoreCase) ||
                     c.StartsWith("BP_NPCCamp_Police_", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var cp in actorsByClass[cls])
                AddPoint("", "enemy_camp", cp.GetProperty("x").GetDouble(), cp.GetProperty("y").GetDouble());
        }

        // Ancient towers (T143): the pickup row of each tower and the lock in front of it.
        using var pickupDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(extractorDir, "pickups.json")));
        var items = new SortedDictionary<string, MapItemDto>(StringComparer.Ordinal);
        void AddItems(JsonElement table)
        {
            foreach (var item in table.EnumerateObject())
                items[item.Name] = new MapItemDto(TextsObj(item.Value.GetProperty("names")), Str(item.Value, "icon"), Str(item.Value, "unlocks"));
        }
        var pickupRows = pickupDoc.RootElement.GetProperty("rows");
        var locks = (actorsByClass.TryGetValue("BP_LevelObject_TowerLockBarrier_C", out var lockList) ? lockList : [])
            .Select(a => (X: a.GetProperty("x").GetDouble(), Y: a.GetProperty("y").GetDouble(),
                Key: a.TryGetProperty("props", out var pr) && pr.TryGetProperty("HintPopupTextId", out var hint) ? Str(hint, "RowName") : null))
            .Where(l => l.Key != null).ToList();
        var towerItems = new HashSet<string>(StringComparer.Ordinal);
        if (actorsByClass.TryGetValue("BP_LevelObject_ItemPickupTower_C", out var towerList))
        {
            foreach (var tower in towerList)
            {
                var props = tower.GetProperty("props");
                var row = props.GetProperty("ItemPickupRowName").GetProperty("Key").GetString()!;
                var contents = pickupRows.TryGetProperty(row, out var list)
                    ? list.EnumerateArray().Select(i => new MapStackDto(i.GetProperty("id").GetString()!, i.GetProperty("count").GetInt32())).ToList()
                    : [];
                foreach (var stack in contents) towerItems.Add(stack.Id);
                double x = tower.GetProperty("x").GetDouble(), y = tower.GetProperty("y").GetDouble();
                var near = locks.Where(l => Math.Abs(l.X - x) < 200 && Math.Abs(l.Y - y) < 200).Select(l => l.Key).FirstOrDefault();
                var hint = near != null ? Bilingual("uiCommon", near) : null;
                AddPoint(Str(props, "LevelObjectInstanceId") ?? $"tower_item:{row}", "pickup_tower", x, y,
                    rk: contents.Count > 0 ? "item" : null, ri: contents.Count > 0 ? contents[0].Id : null, ex: row,
                    items: contents, hint: hint);
            }
        }
        var pickupItems = pickupDoc.RootElement.GetProperty("items");
        foreach (var item in pickupItems.EnumerateObject())
        {
            var unlocks = Str(item.Value, "unlocks");
            if (!towerItems.Contains(item.Name)) continue;
            items[item.Name] = new MapItemDto(TextsObj(item.Value.GetProperty("names")), Str(item.Value, "icon"), unlocks);
            if (unlocks != null && pickupItems.TryGetProperty(unlocks, out var product))
                items[unlocks] = new MapItemDto(TextsObj(product.GetProperty("names")), Str(product, "icon"));
        }

        // Quests and their givers (T145). A quest point carries the ids of its quests, so does a giver and the same NPC's point.
        using var questDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(extractorDir, "quests.json")));
        AddItems(questDoc.RootElement.GetProperty("items"));
        var questTitles = questDoc.RootElement.GetProperty("quests").EnumerateArray()
            .ToDictionary(q => q.GetProperty("id").GetString()!, q => TextsObj(q.GetProperty("names")), StringComparer.OrdinalIgnoreCase);
        var questPoints = questTitles.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var questGivers = questTitles.Keys.ToDictionary(id => id, _ => new List<SortedDictionary<string, string>>(), StringComparer.OrdinalIgnoreCase);
        static bool OnSurface(JsonElement spot) => spot.GetProperty("z").GetDouble() >= UndergroundZ;

        foreach (var giver in questDoc.RootElement.GetProperty("givers").EnumerateArray())
        {
            var unique = giver.GetProperty("unique").GetString()!;
            var names = TextsObj(giver.GetProperty("names"));
            var ids = giver.GetProperty("quests").EnumerateArray().Select(q => q.GetString()!).Where(questTitles.ContainsKey).ToList();
            if (ids.Count == 0) continue;
            foreach (var id in ids)
                if (names.Count > 0 && !questGivers[id].Any(g => g.SequenceEqual(names))) questGivers[id].Add(names);

            var placed = new List<(double X, double Y)>();
            foreach (var spot in giver.GetProperty("spots").EnumerateArray().Where(OnSurface))
            {
                double x = spot.GetProperty("x").GetDouble(), y = spot.GetProperty("y").GetDouble();
                if (MapKey(x, y) == null || placed.Any(p => Math.Abs(p.X - x) < 500 && Math.Abs(p.Y - y) < 500)) continue;
                placed.Add((x, y));
                // The same character can stand there under two unique names (one per quest stage): one marker for both.
                var index = points.FindIndex(p => p.Category == "quest_giver" && Math.Abs(p.X - x) < 500 && Math.Abs(p.Y - y) < 500);
                if (index >= 0)
                {
                    var merged = points[index].Quests!.Union(ids, StringComparer.OrdinalIgnoreCase).ToList();
                    points[index] = points[index] with { Quests = merged, Names = points[index].Names ?? (names.Count > 0 ? names : null) };
                    foreach (var id in ids)
                        if (!questPoints[id].Contains(points[index].Id)) questPoints[id].Add(points[index].Id);
                    continue;
                }
                var pointId = AddPoint($"giver:{unique}", "quest_giver", x, y, names, quests: ids)!;
                foreach (var id in ids) questPoints[id].Add(pointId);
            }

            // The NPC category shows the same character: its card lists the quests too.
            for (var i = 0; i < points.Count; i++)
                if (points[i].Category is "npc" or "merchant" && points[i].Quests == null && placed.Any(p => Math.Abs(p.X - points[i].X) < 10 && Math.Abs(p.Y - points[i].Y) < 10))
                    points[i] = points[i] with { Quests = ids };
        }

        var questPlaces = new Dictionary<string, int>(StringComparer.Ordinal);
        void AddQuestPlace(string quest, string preferred, double x, double y, SortedDictionary<string, string>? names)
        {
            if (MapKey(x, y) == null) return;
            if (questPlaces.TryGetValue(preferred, out var index))
            {
                if (!points[index].Quests!.Contains(quest)) points[index] = points[index] with { Quests = [.. points[index].Quests!, quest] };
                if (!questPoints[quest].Contains(points[index].Id)) questPoints[quest].Add(points[index].Id);
                return;
            }
            var id = AddPoint(preferred, "quest", x, y, names, quests: [quest])!;
            questPlaces[preferred] = points.Count - 1;
            questPoints[quest].Add(id);
        }

        foreach (var quest in questDoc.RootElement.GetProperty("quests").EnumerateArray())
        {
            var id = quest.GetProperty("id").GetString()!;
            foreach (var place in quest.GetProperty("locations").EnumerateArray().Where(OnSurface))
            {
                var row = place.GetProperty("row").GetString()!;
                if (row.StartsWith("AlternativeTracking_", StringComparison.OrdinalIgnoreCase)) continue;
                AddQuestPlace(id, $"quest:{row}", place.GetProperty("x").GetDouble(), place.GetProperty("y").GetDouble(), questTitles[id]);
            }
            foreach (var target in quest.GetProperty("targets").EnumerateArray().Where(OnSurface))
            {
                double x = target.GetProperty("x").GetDouble(), y = target.GetProperty("y").GetDouble();
                var names = TextsObj(target.GetProperty("names"));
                AddQuestPlace(id, string.Create(CultureInfo.InvariantCulture, $"quest-target:{Math.Round(x)}_{Math.Round(y)}"), x, y,
                    names.Count > 0 ? names : questTitles[id]);
            }
        }
        // Materials (ores, oil, crystal, night stone, sky island ore, world tree ore)
        void AddMaterialClass(string cls, string cat)
        {
            if (!actorsByClass.TryGetValue(cls, out var list)) return;
            foreach (var a in list)
            {
                if (a.GetProperty("cell").GetString() == PersistentLevelCell) continue;
                AddPoint("", cat, a.GetProperty("x").GetDouble(), a.GetProperty("y").GetDouble());
            }
        }

        AddMaterialClass("BP_PalMapObjectSpawner_RockCopper_C", "ore");
        AddMaterialClass("BP_PalMapObjectSpawner_RockCoal_C", "coal");
        AddMaterialClass("BP_PalMapObjectSpawner_Sulfur_C", "sulfur");
        AddMaterialClass("BP_PalMapObjectSpawner_RockQuartz_C", "quartz");
        AddMaterialClass("BP_PalMapObjectSpawner_PalCrystal_C", "paldium");
        AddMaterialClass("BP_PalMapObjectSpawner_PalCrystal_Small_C", "paldium");
        AddMaterialClass("BP_LevelObject_OilField_C", "oil");
        AddMaterialClass("BP_PalMapObjectSpawner_Crystal_C", "hexolite");
        AddMaterialClass("BP_PalMapObjectSpawner_NightStone_C", "night_sand");
        AddMaterialClass("BP_PalMapObjectSpawner_SkyIslandOre_C", "sky_ore");
        AddMaterialClass("BP_PalMapObjectSpawner_WorldTreeOre_C", "worldtree_ore");

        var quests = questDoc.RootElement.GetProperty("quests").EnumerateArray().Select(q =>
        {
            var id = q.GetProperty("id").GetString()!;
            return new MapQuestDto(
                id, q.GetProperty("type").GetString()!, questTitles[id], TextsObj(q.GetProperty("descriptions")),
                q.GetProperty("blocks").GetInt32(),
                q.GetProperty("steps").EnumerateArray().Select(s => new MapStepDto(s.GetProperty("block").GetInt32(), TextsObj(s.GetProperty("names")))).ToList(),
                q.GetProperty("exp").GetInt32(),
                q.GetProperty("items").EnumerateArray().Select(i => new MapStackDto(i.GetProperty("id").GetString()!, i.GetProperty("count").GetInt32())).ToList(),
                questGivers[id], questPoints[id]);
        }).ToList();

        // Predators (T149): the species variants and the game's names for them ("<prefix> <species name>").
        var predatorTable = tables.GetProperty("predators");
        var predatorPrefix = TextsObj(predatorTable.GetProperty("prefix"));
        var predators = new List<MapPredatorDto>();
        foreach (var id in predatorTable.GetProperty("species").EnumerateArray().Select(s => s.GetString()!))
        {
            if (PalOf(id) is not { } palId || !palById.TryGetValue(palId, out var pal)) continue;
            var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (lang, prefix) in predatorPrefix)
                names[lang] = prefix + " " + (pal.Names.TryGetValue(lang, out var own) ? own : pal.Names["en"]);
            predators.Add(new MapPredatorDto(palId, names));
        }
        predators.Sort((a, b) => string.CompareOrdinal(a.Pal, b.Pal));

        // A category without a single point on a map picture is left out (e.g. Dog Coins lie inside dungeons only),
        // and so is a category that still awaits its check.
        points.RemoveAll(p => AwaitingCheck.Contains(p.Category));
        var used = points.Select(p => p.Category).ToHashSet(StringComparer.Ordinal);
        if (predators.Count > 0) used.Add("predator");
        return new MapDataFile(1, maps, categories.Where(c => used.Contains(c.Id)).ToList(), regions, points, quests, items, predators);
    }
}
