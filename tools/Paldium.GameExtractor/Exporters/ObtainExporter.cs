using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// Normalized spawn / obtain source tables (no joins, no coordinate transform; the DataBuilder does that).
/// Output: <c>obtain.json</c>.
/// </summary>
public static class ObtainExporter
{
    private const string Spawner = "Pal/Content/Pal/DataTable/Spawner/";
    private const string Dungeon = "Pal/Content/Pal/DataTable/Dungeon/";

    /// <param name="worldActorsPath">The world scan (T135): without it the spawners the two tables miss are left out.</param>
    public static JObject Export(DefaultFileProvider provider, List<string> warnings, string? worldActorsPath = null)
    {
        var wildSpawner = WildSpawner(GameTables.LoadRows(provider, Spawner + "DT_PalWildSpawner"));
        var placements = Placements(GameTables.LoadRows(provider, Spawner + "DT_PalSpawnerPlacement"));
        if (worldActorsPath != null)
        {
            var bossSpawners = GameTables.LoadRows(provider, "Pal/Content/Pal/DataTable/UI/DT_BossSpawnerLoactionData").Properties()
                .Select(r => r.Value["SpawnerID"]?.Value<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            SpawnerSupplement.Apply(provider, wildSpawner, placements, bossSpawners, worldActorsPath, warnings);
        }
        else warnings.Add("spawners: no world scan given (--world): spawners missing from the tables are not added.");

        var result = new JObject
        {
            ["wildSpawner"] = wildSpawner,
            ["placements"] = placements,
            ["dungeonEnemies"] = DungeonEnemies(GameTables.LoadRows(provider, Dungeon + "DT_DungeonEnemySpawnDataTable")),
            ["dungeonAreas"] = DungeonAreas(GameTables.LoadRows(provider, Dungeon + "DT_DungeonSpawnAreaDataTable")),
            ["cages"] = Cages(GameTables.LoadRows(provider, "Pal/Content/Pal/DataTable/Character/DT_CapturedCagePal")),
            ["shops"] = Shops(GameTables.LoadRows(provider, "Pal/Content/Pal/DataTable/PalShop/DT_PalShopCreateData")),
            ["fishing"] = Fishing(GameTables.LoadRows(provider, "Pal/Content/Pal/DataTable/Fishing/DT_PalFishPondLotteryDataTable")),
            ["fishingSpots"] = FishingSpots(GameTables.LoadRows(provider, "Pal/Content/Pal/DataTable/Fishing/DT_PalFishingSpotPalSpawnerDataTable")),
            ["raids"] = Raids(GameTables.LoadRows(provider, "Pal/Content/Pal/Blueprint/RaidBoss/DT_PalRaidBoss")),
            ["worldMapAreas"] = WorldMapAreas(GameTables.LoadRows(provider, "Pal/Content/Pal/DataTable/WorldMapAreaData/DT_WorldMapAreaData")),
        };

        foreach (var (name, value) in result)
            warnings.Add($"obtain.{name}: {((JContainer)value!).Count} entries.");
        return result;
    }

    private static JArray WildSpawner(JObject rows)
    {
        var result = new JArray();
        foreach (var row in rows.Properties())
        {
            var p = (JObject)row.Value;
            var slots = new JArray();
            for (var slot = 1; slot <= 3; slot++)
            {
                var pal = GameTables.Name(p[$"Pal_{slot}"]);
                if (pal == null) continue;
                slots.Add(new JObject
                {
                    ["pal"] = pal,
                    ["levelMin"] = p[$"LvMin_{slot}"],
                    ["levelMax"] = p[$"LvMax_{slot}"],
                });
            }

            result.Add(new JObject
            {
                ["row"] = row.Name,
                ["spawner"] = p["SpawnerName"],
                ["type"] = GameTables.Enum(p["SpawnerType"]),
                ["weight"] = p["Weight"],
                ["onlyTime"] = GameTables.Enum(p["OnlyTime"]),
                ["worldTreeAura"] = p["bHasWorldTreeAura"],
                ["slots"] = slots,
            });
        }

        return result;
    }

    private static JArray Placements(JObject rows)
    {
        var result = new JArray();
        foreach (var row in rows.Properties())
        {
            var p = (JObject)row.Value;
            result.Add(new JObject
            {
                ["spawner"] = p["SpawnerName"],
                ["placement"] = GameTables.Enum(p["PlacementType"]),
                ["x"] = Math.Round(p["Location"]!["X"]!.Value<double>(), 1),
                ["y"] = Math.Round(p["Location"]!["Y"]!.Value<double>(), 1),
                ["radius"] = (int)Math.Round(p["StaticRadius"]?.Value<double>() ?? 0),
            });
        }

        return result;
    }

    private static JArray DungeonEnemies(JObject rows) => new(rows.Properties().Select(r => new JObject
    {
        ["area"] = r.Value["SpawnAreaId"],
        ["rank"] = GameTables.Enum(r.Value["RankType"]),
        ["spawner"] = r.Value["SpawnerName"],
    }));

    private static JObject DungeonAreas(JObject rows)
    {
        var result = new JObject();
        foreach (var row in rows.Properties()) result[row.Name] = row.Value["DungeonNameTextId"];
        return result;
    }

    private static JArray Cages(JObject rows) => new(rows.Properties().Select(r => new JObject
    {
        ["field"] = r.Value["FieldName"],
        ["pal"] = GameTables.Name(r.Value["PalId"]),
        ["levelMin"] = r.Value["MinLevel"],
        ["levelMax"] = r.Value["MaxLevel"],
        ["weight"] = r.Value["Weight"],
    }));

    private static JArray Shops(JObject rows) => new(rows.Properties().Select(r => new JObject
    {
        ["shop"] = r.Name,
        ["levelMin"] = r.Value["MinCharacterLevel"],
        ["levelMax"] = r.Value["MaxCharacterLevel"],
        ["pals"] = new JArray((r.Value["CharacterIDArray"] as JArray ?? [])
            .Select(c => GameTables.Name(c["Key"])).Where(n => n != null)),
    }));

    private static JArray Fishing(JObject rows) => new(rows.Properties().Select(r => new JObject
    {
        ["lottery"] = r.Value["LotteryName"],
        ["group"] = r.Value["GainItemLotteryName"],
        ["pal"] = GameTables.Name(r.Value["CharacterID"]),
        ["levelMin"] = r.Value["CharacterLevelMin"],
        ["levelMax"] = r.Value["CharacterLevelMax"],
        ["weight"] = r.Value["Weight"],
    }));

    private static JArray FishingSpots(JObject rows) => new(rows.Properties().Select(r => new JObject
    {
        ["pal"] = GameTables.Name(r.Value["PalName"]?["Key"]),
        ["levelMin"] = r.Value["LevelMin"],
        ["levelMax"] = r.Value["LevelMax"],
    }));

    private static JArray Raids(JObject rows)
    {
        var result = new JArray();
        foreach (var row in rows.Properties())
            foreach (var info in row.Value["InfoList"] as JArray ?? [])
                result.Add(new JObject
                {
                    ["summon"] = row.Name,
                    ["pal"] = GameTables.Name(info["PalId"]?["Key"]),
                    ["level"] = info["Level"],
                });
        return result;
    }

    private static JObject WorldMapAreas(JObject rows)
    {
        var result = new JObject();
        foreach (var row in rows.Properties()) result[row.Name] = row.Value["MsgID"];
        return result;
    }
}
