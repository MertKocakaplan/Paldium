using System.Globalization;
using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// Exports tabular map metadata (<c>map-tables.json</c>) used by DataBuilder alongside <c>world-actors.json</c>:
/// alpha bosses, respawn points, area names, wide/Tree region names, notes,
/// fast-travel names, and ore/material names.
/// </summary>
public static class MapTablesExporter
{
    private const string BossLocationTable = "Pal/Content/Pal/DataTable/UI/DT_BossSpawnerLoactionData";
    private const string RespawnTable = "Pal/Content/Pal/DataTable/UI/DT_RespawnPointInfo";
    private const string AreaTable = "Pal/Content/Pal/DataTable/WorldMapAreaData/DT_WorldMapAreaData";
    private const string NoteMasterTable = "Pal/Content/Pal/DataTable/NoteData/DT_NoteMasterDataTable";

    internal static readonly (string Key, string ItemId)[] MaterialItems =
    [
        ("ore", "CopperOre"),
        ("coal", "Coal"),
        ("sulfur", "Sulfur"),
        ("quartz", "Quartz"),
        ("paldium", "Pal_crystal_S"),
        ("oil", "CrudeOil"),
        ("hexolite", "RainbowCrystal"),
        ("night_sand", "NightStone"),
        ("sky_ore", "SkyIslandOre"),
        ("worldtree_ore", "WorldTreeOre"),
    ];

    public static JObject Export(
        DefaultFileProvider provider,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts,
        List<string> warnings)
    {
        var (bosses, bounties, oilRigs) = ExportBossesAndBounties(provider, texts);
        var respawns = ExportRespawns(provider, texts);
        var areas = ExportAreas(provider, texts);
        var wideRegions = ExportWideRegions(texts);
        var notes = ExportNotes(provider, texts);
        var fastTravelNames = ExportTableDict(texts, "respawnText");
        var materials = ExportMaterialNames(texts);

        warnings.Add($"map-tables: {bosses.Count} alphas, {bounties.Count} bounties, {oilRigs.Count} oilRigs, {respawns.Count} respawns, {areas.Count} areas, {wideRegions.Count} wideRegions, {notes.Count} notes.");
        return new JObject
        {
            ["bosses"] = bosses,
            ["bounties"] = bounties,
            ["oilRigs"] = oilRigs,
            ["respawns"] = respawns,
            ["areas"] = areas,
            ["wideRegions"] = wideRegions,
            ["notes"] = notes,
            ["fastTravelNames"] = fastTravelNames,
            ["materials"] = materials,
            ["relicClasses"] = ExportRelicClasses(provider, warnings),
            ["towerBosses"] = ExportTowerBosses(provider, texts, warnings),
            ["predators"] = ExportPredators(provider, texts, warnings),
        };
    }

    /// <summary>
    /// The battle name of a boss is the Pal name text <c>PAL_NAME_&lt;type&gt;</c> ("Zoe &amp; Grizzbolt"), the one the tower's <c>GYM_</c> species uses.
    /// Two are spelled otherwise: their <c>GYM_</c> rows carry the title of the organisation that the tower is named after (PIDF; Brothers of the
    /// Eternal Pyre).
    /// </summary>
    private static readonly Dictionary<string, string> BossNameKeys = new(StringComparer.Ordinal)
    {
        ["DesertBoss"] = "PAL_NAME_DessertBoss",
        ["ElectricBoss"] = "PAL_NAME_VolcanoBoss",
    };

    /// <summary>
    /// T148: the tower bosses by <c>EPalBossType</c> name: the tower's name (UI text <c>BOSS_BATTLE_NAME_&lt;type&gt;</c>, the key a save lists a
    /// defeated tower under) and the boss's battle name.
    /// </summary>
    private static JObject ExportTowerBosses(DefaultFileProvider provider,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, List<string> warnings)
    {
        var result = new JObject();
        foreach (var key in texts["en"]["uiCommon"].Keys.Where(k => k.StartsWith("BOSS_BATTLE_NAME_", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var type = key["BOSS_BATTLE_NAME_".Length..];
            var textId = BossNameKeys.GetValueOrDefault(type, "PAL_NAME_" + type);
            var names = new JObject();
            var bossNames = new JObject();
            foreach (var language in TextExporter.Languages)
            {
                if (texts[language]["uiCommon"].TryGetValue(key, out var name) && name.Trim().Trim('?', '？').Length > 0)
                    names[language] = TextExporter.Plain(name, texts[language]).Trim();
                if (texts[language]["palName"].TryGetValue(textId, out var boss) && boss.Trim() is not ("" or "-" or "???"))
                    bossNames[language] = TextExporter.Plain(boss, texts[language]).Trim();
            }
            result[type] = new JObject { ["key"] = key, ["names"] = names, ["boss"] = bossNames };
        }
        warnings.Add($"map-tables: tower bosses without a boss name: {string.Join(", ", result.Properties().Where(p => !((JObject)p.Value["boss"]!).HasValues).Select(p => p.Name))}");
        return result;
    }

    /// <summary>
    /// T149: the predator (rampaging) Pals: the <c>PREDATOR_&lt;species&gt;</c> rows of the species table with the game's name prefix. The rows ending in
    /// <c>_Quest</c> are the ones a quest places; they are listed apart.
    /// </summary>
    private static JObject ExportPredators(DefaultFileProvider provider,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, List<string> warnings)
    {
        var species = new JArray();
        var quest = new JArray();
        foreach (var row in GameTables.LoadRows(provider, SpeciesExporter.MonsterTable).Properties()
                     .Where(p => p.Name.StartsWith("PREDATOR_", StringComparison.OrdinalIgnoreCase)))
        {
            var id = row.Name["PREDATOR_".Length..];
            if (id.EndsWith("_Quest", StringComparison.OrdinalIgnoreCase)) quest.Add(id);
            else species.Add(id);
        }
        var prefix = new JObject();
        foreach (var language in TextExporter.Languages)
        {
            if (texts[language]["namePrefix"].TryGetValue("PREDATOR_NAME", out var text)) prefix[language] = text.Trim();
        }
        warnings.Add($"map-tables: {species.Count} predator species, {quest.Count} quest variants.");
        return new JObject { ["prefix"] = prefix, ["species"] = species, ["quest"] = quest };
    }

    /// <summary>The effigy actor classes and the type each one gives (<c>RelicType</c> of the class defaults; the base class has none: type 0).</summary>
    private static JObject ExportRelicClasses(DefaultFileProvider provider, List<string> warnings)
    {
        var result = new JObject();
        const string folder = "Pal/Content/Pal/Blueprint/MapObject/Object/LevelObject/";
        foreach (var path in provider.Files.Keys
                     .Where(k => k.StartsWith(folder + "BP_LevelObject_Relic", StringComparison.OrdinalIgnoreCase) &&
                                 k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                var exports = JArray.Parse(JsonConvert.SerializeObject(provider.LoadPackage(path).GetExports().ToList()));
                var defaults = exports.OfType<JObject>()
                    .FirstOrDefault(e => e["Name"]?.Value<string>()?.StartsWith("Default__", StringComparison.Ordinal) == true);
                if (defaults == null) continue;
                result[Path.GetFileNameWithoutExtension(path) + "_C"] = GameTables.Enum(defaults["Properties"]?["RelicType"]) ?? "CapturePower";
            }
            catch (Exception ex)
            {
                warnings.Add($"map-tables effigy {path}: {ex.Message}");
            }
        }
        return result;
    }

    private static (JArray Alphas, JArray Bounties, JArray OilRigs) ExportBossesAndBounties(
        DefaultFileProvider provider,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts)
    {
        var rows = GameTables.LoadRows(provider, BossLocationTable);
        var alphas = new JArray();
        var bounties = new JArray();
        var oilRigs = new JArray();
        var seenBounties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows.Properties())
        {
            var p = (JObject)row.Value;
            if (p["Location"] is not JObject loc) continue;
            var spawnerId = p["SpawnerID"]?.Value<string>();
            if (string.IsNullOrEmpty(spawnerId) || spawnerId == "None") continue;
            var charId = GameTables.Name(p["CharacterID"]);
            var level = p["Level"]?.Value<int>() ?? 0;
            double x = Math.Round(loc["X"]!.Value<double>(), 1);
            double y = Math.Round(loc["Y"]!.Value<double>(), 1);
            double z = Math.Round(loc["Z"]!.Value<double>(), 1);

            if (charId != null)
            {
                alphas.Add(new JObject
                {
                    ["spawnerId"] = spawnerId,
                    ["characterId"] = charId,
                    ["level"] = level,
                    ["x"] = x,
                    ["y"] = y,
                    ["z"] = z,
                });
                continue;
            }

            if (spawnerId.StartsWith("REGION_Oilrig", StringComparison.OrdinalIgnoreCase))
            {
                if (!seenRigs.Add(spawnerId)) continue;
                var names = new JObject();
                foreach (var lang in TextExporter.Languages)
                {
                    if (texts[lang]["worldMap"].TryGetValue(spawnerId, out var nm) && !string.IsNullOrWhiteSpace(nm))
                        names[lang] = TextExporter.Plain(nm, texts[lang]).Trim();
                }
                oilRigs.Add(new JObject
                {
                    ["id"] = spawnerId,
                    ["level"] = level,
                    ["names"] = names,
                    ["x"] = x,
                    ["y"] = y,
                    ["z"] = z,
                });
                continue;
            }

            // Human Bounty target (row duplicated twice in DT_BossSpawnerLoactionData)
            if (!seenBounties.Add(spawnerId)) continue;
            var bNames = new JObject();
            foreach (var lang in TextExporter.Languages)
            {
                if ((texts[lang]["humanName"].TryGetValue("NAME_" + spawnerId, out var nm) ||
                     texts[lang]["humanName"].TryGetValue(spawnerId, out nm)) &&
                    !string.IsNullOrWhiteSpace(nm) && nm.Trim() is not ("-" or "???"))
                {
                    bNames[lang] = nm.Trim();
                }
            }
            bounties.Add(new JObject
            {
                ["id"] = spawnerId,
                ["level"] = level,
                ["names"] = bNames,
                ["x"] = x,
                ["y"] = y,
                ["z"] = z,
            });
        }

        return (alphas, bounties, oilRigs);
    }

    private static JObject ExportRespawns(
        DefaultFileProvider provider,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts)
    {
        var rows = GameTables.LoadRows(provider, RespawnTable);
        var obj = new JObject();
        foreach (var row in rows.Properties())
        {
            var id = row.Value["SpawnPointID"]?.Value<string>() ?? row.Name;
            var names = new JObject();
            foreach (var lang in TextExporter.Languages)
            {
                if (texts[lang]["respawnText"].TryGetValue(id, out var val) && !string.IsNullOrWhiteSpace(val))
                    names[lang] = val.Trim();
            }
            obj[id] = new JObject
            {
                ["forPvP"] = row.Value["ForPvP"]?.Value<bool>() ?? false,
                ["names"] = names,
            };
        }
        return obj;
    }

    private static JObject ExportAreas(
        DefaultFileProvider provider,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts)
    {
        var rows = GameTables.LoadRows(provider, AreaTable);
        var obj = new JObject();
        foreach (var row in rows.Properties())
        {
            var msgId = row.Value["MsgID"]?.Value<string>() ?? "";
            var names = new JObject();
            foreach (var lang in TextExporter.Languages)
            {
                if (texts[lang]["worldMap"].TryGetValue(msgId, out var val) && !string.IsNullOrWhiteSpace(val))
                    names[lang] = TextExporter.Plain(val, texts[lang]).Trim();
            }
            obj[row.Name] = new JObject
            {
                ["msgId"] = msgId,
                ["names"] = names,
            };
        }
        return obj;
    }

    private static JObject ExportWideRegions(Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts)
    {
        var obj = new JObject();
        foreach (var key in texts["en"]["worldMap"].Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!key.StartsWith("REGION_Wide_", StringComparison.OrdinalIgnoreCase) &&
                !key.StartsWith("REGION_WorldTree", StringComparison.OrdinalIgnoreCase))
                continue;
            var names = new JObject();
            foreach (var lang in TextExporter.Languages)
            {
                if (texts[lang]["worldMap"].TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                    names[lang] = TextExporter.Plain(val, texts[lang]).Trim();
            }
            if (names.Count > 0) obj[key] = names;
        }
        return obj;
    }

    private static JObject ExportNotes(
        DefaultFileProvider provider,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts)
    {
        var rows = GameTables.LoadRows(provider, NoteMasterTable);
        var obj = new JObject();
        foreach (var row in rows.Properties())
        {
            var tid = row.Value["TextId_Description"]?.Value<string>() ?? row.Name;
            var names = new JObject();
            foreach (var lang in TextExporter.Languages)
            {
                if (texts[lang]["noteDesc"].TryGetValue(tid, out var raw))
                {
                    var firstLine = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(firstLine)) names[lang] = firstLine;
                }
            }
            obj[row.Name] = new JObject
            {
                ["textId"] = tid,
                ["names"] = names,
            };
        }
        return obj;
    }

    private static JObject ExportTableDict(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts,
        string tableName)
    {
        var obj = new JObject();
        foreach (var key in texts["en"][tableName].Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var names = new JObject();
            foreach (var lang in TextExporter.Languages)
            {
                if (texts[lang][tableName].TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                    names[lang] = val.Trim();
            }
            if (names.Count > 0) obj[key] = names;
        }
        return obj;
    }

    private static JObject ExportMaterialNames(Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts)
    {
        var obj = new JObject();
        foreach (var (key, itemId) in MaterialItems)
        {
            var names = new JObject();
            foreach (var lang in TextExporter.Languages)
            {
                if (texts[lang]["itemName"].TryGetValue("ITEM_NAME_" + itemId, out var val) && !string.IsNullOrWhiteSpace(val))
                    names[lang] = val.Trim();
            }
            obj[key] = names;
        }
        return obj;
    }
}
