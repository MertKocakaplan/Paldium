using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// T135: the spawn tables and the placement table do not cover each other. Both gaps are filled from the level itself: every Pal spawner
/// actor of the world scan names its spawner in its Blueprint's class defaults (<c>SpawnerName</c>) and carries its groups there
/// (<c>SpawnGroupList</c>).
/// <list type="bullet">
/// <item>A spawn table spawner without a placement row gets the positions of its actors. A field boss only when the game's boss location
/// table lists it: the other boss spawners in the level are old copies of bosses placed elsewhere.</item>
/// <item>A placement without a spawn table row (e.g. the desert Suzaku) gets its rows from the Blueprint's groups.</item>
/// </list>
/// </summary>
public static class SpawnerSupplement
{
    /// <summary>The spawn radius of every field boss row and of 99.6 % of the field rows of <c>DT_PalSpawnerPlacement</c>.</summary>
    private const int TableRadius = 15000;

    /// <summary>Actors below this height are inside the dungeon level instances.</summary>
    private const double UndergroundZ = -20000;

    private sealed record Spawner(string Name, bool FieldBoss, JArray Groups);

    public static void Apply(DefaultFileProvider provider, JArray wildSpawner, JArray placements, IReadOnlySet<string> bossSpawners,
        string worldActorsPath, List<string> warnings)
    {
        var world = JObject.Parse(File.ReadAllText(worldActorsPath));
        var classes = world.Properties()
            .Where(p => p.Name.StartsWith("BP_PalSpawner_", StringComparison.OrdinalIgnoreCase) && p.Name.EndsWith("_C", StringComparison.Ordinal))
            .ToList();
        var assets = provider.Files.Keys
            .Where(k => k.StartsWith("Pal/Content/Pal/Blueprint/Spawner/", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .GroupBy(k => Path.GetFileNameWithoutExtension(k), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var byName = new Dictionary<string, (Spawner Info, List<JObject> Actors)>(StringComparer.OrdinalIgnoreCase);
        var unread = 0;
        foreach (var property in classes)
        {
            if (!assets.TryGetValue(property.Name[..^2], out var path) || Read(provider, path, warnings) is not { } info)
            {
                unread++;
                continue;
            }
            if (!byName.TryGetValue(info.Name, out var entry)) byName[info.Name] = entry = (info, []);
            entry.Actors.AddRange(((JArray)property.Value).OfType<JObject>().Where(a => (a["z"]?.Value<double>() ?? 0) >= UndergroundZ));
        }

        var tableNames = wildSpawner.Select(w => w["spawner"]!.Value<string>()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var placedNames = placements.Select(p => p["spawner"]!.Value<string>()!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Spawn table spawners without a placement row.
        var found = new List<string>();
        var oldBosses = new List<string>();
        var missing = new List<string>();
        var points = 0;
        foreach (var name in tableNames.Where(n => !placedNames.Contains(n)).Order(StringComparer.Ordinal))
        {
            var rows = wildSpawner.Where(w => string.Equals(w["spawner"]!.Value<string>(), name, StringComparison.OrdinalIgnoreCase)).ToList();
            var boss = rows.Any(r => r["type"]?.Value<string>() == "FieldBoss");
            if (!byName.TryGetValue(name, out var entry) || entry.Actors.Count == 0)
            {
                missing.Add($"{name} ({Pals(rows)})");
                continue;
            }
            if (boss && !bossSpawners.Contains(name))
            {
                oldBosses.Add($"{name} ({Pals(rows)})");
                continue;
            }
            foreach (var actor in entry.Actors)
            {
                placements.Add(new JObject
                {
                    ["spawner"] = name,
                    ["placement"] = boss ? "FieldBoss" : "Field",
                    ["x"] = Math.Round(actor["x"]!.Value<double>(), 1),
                    ["y"] = Math.Round(actor["y"]!.Value<double>(), 1),
                    ["radius"] = TableRadius,
                });
                points++;
            }
            found.Add($"{name} ({Pals(rows)}; {entry.Actors.Count})");
        }

        // 2. Placed spawners the spawn table does not know.
        var added = new List<string>();
        var unknown = new List<string>();
        foreach (var name in placements.Where(p => p["placement"]?.Value<string>() is "Field" or "FieldBoss")
                     .Select(p => p["spawner"]!.Value<string>()!).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(n => !tableNames.Contains(n)).Order(StringComparer.Ordinal).ToList())
        {
            if (!byName.TryGetValue(name, out var entry))
            {
                unknown.Add(name);
                continue;
            }
            var rows = Rows(name, entry.Info);
            foreach (var row in rows) wildSpawner.Add(row);
            if (rows.Count > 0) added.Add($"{name} ({Pals(rows)})");
            else unknown.Add(name);
        }

        warnings.Add($"spawners: {classes.Count} spawner classes in the level ({unread} unread), {byName.Count} spawner names.");
        warnings.Add($"spawners: without a placement row, found in the level: {found.Count} ({points} points): {string.Join("; ", found)}");
        warnings.Add($"spawners: boss spawners in the level that the boss table does not list: {oldBosses.Count}: {string.Join("; ", oldBosses)}");
        warnings.Add($"spawners: not found in the level: {missing.Count}: {string.Join("; ", missing)}");
        warnings.Add($"spawners: placements without a table row, read from the Blueprint: {added.Count}: {string.Join("; ", added)}");
        warnings.Add($"spawners: placements without a table row and without a Blueprint: {unknown.Count}: {string.Join("; ", unknown)}");
    }

    private static string Pals(IEnumerable<JToken> rows) => string.Join(", ", rows
        .SelectMany(r => (JArray)r["slots"]!).Select(s => s["pal"]!.Value<string>()).Distinct(StringComparer.Ordinal));

    private static List<JObject> Rows(string name, Spawner spawner)
    {
        var rows = new List<JObject>();
        var index = 0;
        foreach (var group in spawner.Groups.OfType<JObject>())
        {
            index++;
            var slots = new JArray();
            foreach (var entry in (group["PalList"] as JArray ?? []).OfType<JObject>())
            {
                if (GameTables.Name(entry["PalId"]?["Key"]) is not { } pal) continue;
                var level = entry["Level"]?.Value<int>() ?? 0;
                slots.Add(new JObject { ["pal"] = pal, ["levelMin"] = level, ["levelMax"] = entry["Level_Max"]?.Value<int>() ?? level });
            }
            if (slots.Count == 0) continue;
            rows.Add(new JObject
            {
                ["row"] = $"{name}_BP{index}",
                ["spawner"] = name,
                ["type"] = spawner.FieldBoss ? "FieldBoss" : "Common",
                ["weight"] = group["Weight"]?.Value<double>() ?? 0,
                ["onlyTime"] = GameTables.Enum(group["OnlyTime"]),
                ["worldTreeAura"] = group["bHasWorldTreeAura"]?.Value<bool>() ?? false,
                ["slots"] = slots,
            });
        }
        return rows;
    }

    /// <summary>The spawner name, kind and groups of a spawner Blueprint, the parents' values first.</summary>
    private static Spawner? Read(DefaultFileProvider provider, string path, List<string> warnings, int depth = 0)
    {
        try
        {
            var exports = JArray.Parse(JsonConvert.SerializeObject(provider.LoadPackage(path).GetExports().ToList())).OfType<JObject>().ToList();
            var properties = exports.FirstOrDefault(e => e["Name"]?.Value<string>()?.StartsWith("Default__", StringComparison.Ordinal) == true)?["Properties"] as JObject;
            Spawner? parent = null;
            var super = exports.Where(e => e["Type"]?.Value<string>() == "BlueprintGeneratedClass")
                .Select(e => e["Super"]?["ObjectPath"]?.Value<string>()).FirstOrDefault(p => p != null);
            if ((properties?["SpawnerName"] == null || properties["SpawnGroupList"] == null) && depth < 4 &&
                super != null && super.StartsWith("Pal/Content/", StringComparison.Ordinal))
                parent = Read(provider, super[..super.LastIndexOf('.')] + ".uasset", warnings, depth + 1);
            var name = GameTables.Name(properties?["SpawnerName"]) ?? parent?.Name;
            if (name == null) return null;
            var type = properties?["SpawnerType"]?.Value<string>();
            return new Spawner(name,
                type != null ? type.EndsWith("FieldBoss", StringComparison.Ordinal) : parent?.FieldBoss ?? false,
                properties?["SpawnGroupList"] as JArray ?? parent?.Groups ?? []);
        }
        catch (Exception ex)
        {
            warnings.Add($"spawners: {path} could not be read: {ex.Message}");
            return null;
        }
    }
}
