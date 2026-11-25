using System.Globalization;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// T145: the main and side quests (<c>DT_PalQuestData</c>; <c>Hidden</c> rows and <c>Test_*</c> rows are left out) and who gives them.
/// <list type="bullet">
/// <item>Name, description and rewards come from the quest Blueprint's class defaults (its parents' values first).</item>
/// <item>A step is one quest block; its text is the UI text key the block Blueprint reads (a constant in its bytecode).</item>
/// <item>Places are the <c>DT_PalQuestLocationData</c> rows the blocks point at and the quest target NPC spawners whose character
/// a block names.</item>
/// <item>A giver is an NPC whose conversation graph (<c>DT_NPCTalkFlow</c>, else the unique NPC's talk Blueprint) has an
/// order-quest node with that quest id. No quest is linked by a similar name.</item>
/// </list>
/// Output: <c>quests.json</c>.
/// </summary>
public static partial class QuestExporter
{
    private const string QuestDataTable = "Pal/Content/Pal/DataTable/Quest/DT_PalQuestData";
    private const string QuestLocationTable = "Pal/Content/Pal/DataTable/Quest/DT_PalQuestLocationData";
    private const string TalkFlowTable = "Pal/Content/Pal/DataTable/NPCTalk/DT_NPCTalkFlow";
    private const string UniqueNpcTable = "Pal/Content/Pal/DataTable/Character/DT_UniqueNPC";
    private const string HumanParamTable = "Pal/Content/Pal/DataTable/Character/DT_PalHumanParameter_Common";
    private const string TalkGraphFolder = "Pal/Content/Pal/Blueprint/FlowGraph/NPCTalkFlow/Graph/";
    private const string OrderNode = "FNBP_OrderQuest";
    private const string BossLocationTable = "Pal/Content/Pal/DataTable/UI/DT_BossSpawnerLoactionData";

    public static JObject Export(DefaultFileProvider provider, ItemCatalog items,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, List<string> warnings, string? worldActorsPath)
    {
        var world = worldActorsPath != null && File.Exists(worldActorsPath) ? JObject.Parse(File.ReadAllText(worldActorsPath)) : null;
        if (world == null) warnings.Add("quests: no world scan, so no givers' places and no quest targets.");
        var blueprints = new Blueprints(provider, warnings);
        var locations = GameTables.LoadRows(provider, QuestLocationTable);
        var targets = ReadTargets(provider, world, texts, blueprints);
        var bossPals = GameTables.LoadRows(provider, BossLocationTable).Properties()
            .Select(r => (Spawner: GameTables.Name(r.Value["SpawnerID"]), Pal: GameTables.Name(r.Value["CharacterID"])))
            .Where(b => b.Spawner != null && b.Pal != null)
            .GroupBy(b => b.Spawner!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Pal!, StringComparer.OrdinalIgnoreCase);

        var quests = new JArray();
        var rewardItems = new SortedSet<string>(StringComparer.Ordinal);
        var withoutName = new List<string>();
        var placeholders = new List<string>();
        var stepless = new List<string>();
        foreach (var row in GameTables.LoadRows(provider, QuestDataTable).Properties())
        {
            var type = GameTables.Enum(row.Value["QuestType"]);
            if (type is not ("Main" or "Sub") || row.Name.StartsWith("Test", StringComparison.OrdinalIgnoreCase)) continue;
            var asset = ToPackagePath(row.Value["QuestData"]?["AssetPathName"]?.Value<string>());
            var props = asset == null ? null : blueprints.Defaults(asset);
            if (props == null)
            {
                warnings.Add($"quests: {row.Name} has no readable quest Blueprint.");
                continue;
            }

            var steps = new JArray();
            var rows = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (locations[row.Name] != null) rows.Add(row.Name);
            var mentioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A save counts the steps of a quest by block group: each group is one step of the quest log.
            var groups = (props["QuestBlockGroupList"] as JArray ?? []).OfType<JObject>().ToList();
            var grouped = groups.SelectMany((g, group) => (g["BlockList"] as JArray ?? []).OfType<JObject>()
                    .Select(b => (Group: group, Path: ToPackagePath(b["AssetPathName"]?.Value<string>()))))
                .Where(b => b.Path != null).ToList();
            var blocks = grouped.Select(b => b.Path!).ToList();
            var variables = blocks.Select(b => Variables(blueprints.Defaults(b), bossPals, items, texts)).ToList();
            for (var index = 0; index < blocks.Count; index++)
            {
                var strings = blueprints.Strings(blocks[index]);
                foreach (var value in strings)
                {
                    if (locations[value] != null) rows.Add(value);
                    if (targets.Any(t => t.Human != null && string.Equals(t.Human, value, StringComparison.OrdinalIgnoreCase))) mentioned.Add(value);
                }
                foreach (var key in StepKeys(strings, texts))
                {
                    // The block's own values first, then those of the quest's other blocks (a "return and talk" step names the count).
                    var raw = Text(texts, "uiCommon", key);
                    var names = Fill(raw, variables.Prepend(variables[index]).ToList());
                    if (names == null) placeholders.Add($"{row.Name} {key}");
                    else if (names.Count > 0 && !steps.Any(s => JToken.DeepEquals(s["names"], names)))
                        steps.Add(new JObject { ["block"] = grouped[index].Group, ["names"] = names });
                }
            }
            if (steps.Count == 0) stepless.Add(row.Name);

            var rewards = props["CommonRewardData"] as JObject;
            var rewardList = new JArray();
            foreach (var entry in (rewards?["Items"] as JArray ?? []).OfType<JObject>())
            {
                var id = GameTables.Name(entry["Key"]?["Key"] ?? entry["Key"]);
                var count = entry["Value"]?.Value<int>() ?? 0;
                if (id == null || count <= 0) continue;
                rewardItems.Add(id);
                rewardList.Add(new JObject { ["id"] = id, ["count"] = count });
            }

            var title = Text(texts, "uiCommon", GameTables.Name(props["QuestTitleMsgId"]));
            if (title.Count == 0) withoutName.Add(row.Name);
            quests.Add(new JObject
            {
                ["id"] = row.Name,
                ["type"] = type,
                ["names"] = title,
                ["descriptions"] = Fill(Text(texts, "uiCommon", GameTables.Name(props["QuestDescriptionMsgId"])), variables) ?? new JObject(),
                ["blocks"] = groups.Count,
                ["steps"] = steps,
                ["exp"] = rewards?["Exp"]?.Value<int>() ?? 0,
                ["items"] = rewardList,
                ["locations"] = new JArray(rows.Select(name => Location(name, locations[name] as JObject)).Where(l => l != null)),
                ["targets"] = new JArray(targets.Where(t => t.Human != null && mentioned.Contains(t.Human) ||
                    string.Equals(t.Quest, row.Name, StringComparison.OrdinalIgnoreCase)).Select(t => new JObject
                {
                    ["x"] = t.X, ["y"] = t.Y, ["z"] = t.Z, ["names"] = t.Names.DeepClone(),
                })),
            });
        }

        var known = quests.Select(q => q["id"]!.Value<string>()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var givers = ReadGivers(provider, blueprints, world, texts, known, warnings);

        var itemTable = new JObject();
        foreach (var id in rewardItems)
            itemTable[id] = new JObject { ["names"] = items.Names(id), ["icon"] = items.Icon(id) };

        var given = givers.SelectMany(g => g["quests"]!.Values<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subs = quests.Where(q => q["type"]!.Value<string>() == "Sub").ToList();
        warnings.Add($"quests: {quests.Count(q => q["type"]!.Value<string>() == "Main")} main, {subs.Count} side; " +
                     $"{subs.Count(q => given.Contains(q["id"]!.Value<string>()!))} side quests with a giver; {givers.Count} givers; {targets.Count} quest target spawners.");
        warnings.Add($"quests: side quests without a giver: {string.Join(", ", subs.Select(q => q["id"]!.Value<string>()!).Where(id => !given.Contains(id)))}");
        warnings.Add($"quests: without a name text: {string.Join(", ", withoutName)}");
        warnings.Add($"quests: without a step text: {stepless.Count}: {string.Join(", ", stepless)}");
        warnings.Add($"quests: step texts left out (a value the data does not hold): {placeholders.Count}: {string.Join("; ", placeholders)}");

        return new JObject
        {
            ["source"] = "DT_PalQuestData + quest and block Blueprints; DT_PalQuestLocationData; DT_NPCTalkFlow talk graphs (order-quest nodes); world scan spawners",
            ["quests"] = quests,
            ["givers"] = givers,
            ["items"] = itemTable,
        };
    }

    /// <summary>The objective keys a block reads. Blocks also name other UI texts (tutorials, button hints); only objectives are steps.</summary>
    private static IEnumerable<string> StepKeys(IReadOnlyList<string> strings, Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts) =>
        strings.Where(s => s.Contains("OBJECT", StringComparison.OrdinalIgnoreCase) && texts["en"]["uiCommon"].ContainsKey(s))
            .Distinct(StringComparer.Ordinal);

    /// <summary>The values a block's step text can name: its required count, item and Pal.</summary>
    private static Dictionary<string, Dictionary<string, string>> Variables(JObject? block, IReadOnlyDictionary<string, string> bossPals,
        ItemCatalog items, Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (block == null) return result;
        void Same(string name, string value) => result[name] = TextExporter.Languages.ToDictionary(l => l, _ => value);

        if (block["RequireCount"]?.Value<int>() is { } count and > 0) Same("TargetNumber", count.ToString(CultureInfo.InvariantCulture));
        if (block["RequireItemMap"] is JArray { Count: 1 } required && GameTables.Name(required[0]["Key"]?["Key"]) is { } item)
        {
            Same("TargetNumber", (required[0]["Value"]?.Value<int>() ?? 0).ToString(CultureInfo.InvariantCulture));
            var names = items.Names(item).Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
            result["TargetItem"] = result["TargetFood"] = names;
        }

        var pal = RowName(block["CountPalName"]) ??
                  (GameTables.Name(block["BossSpawnerName"]) is { } spawner && bossPals.TryGetValue(spawner, out var boss) ? boss : null);
        if (pal != null)
        {
            if (pal.StartsWith("BOSS_", StringComparison.OrdinalIgnoreCase)) pal = pal[5..];
            result["targetpal"] = TextExporter.Languages.Select(l => (Language: l, Name: texts[l]["palName"]
                    .FirstOrDefault(p => string.Equals(p.Key, "PAL_NAME_" + pal, StringComparison.OrdinalIgnoreCase)).Value))
                .Where(p => p.Name != null).ToDictionary(p => p.Language, p => p.Name.Trim());
        }
        return result;
    }

    /// <summary>The texts with their variables filled; null when a variable has no value (the text is then not shown).</summary>
    private static JObject? Fill(JObject names, IReadOnlyList<Dictionary<string, Dictionary<string, string>>> variables)
    {
        var result = new JObject();
        foreach (var property in names.Properties())
        {
            var text = VariableTag().Replace(property.Value.ToString(), match =>
            {
                foreach (var set in variables)
                {
                    if (set.TryGetValue(match.Groups[1].Value, out var byLanguage) && byLanguage.TryGetValue(property.Name, out var value))
                        return value;
                }
                return match.Value;
            });
            if (VariableTag().IsMatch(text)) return null;
            result[property.Name] = text;
        }
        return result;
    }

    [GeneratedRegex(@"\{([A-Za-z_]+)\}")]
    private static partial Regex VariableTag();

    private static JObject? Location(string row, JObject? data) => data?["Position"] is JObject position
        ? new JObject
        {
            ["row"] = row,
            ["x"] = Math.Round(position["X"]!.Value<double>(), 1),
            ["y"] = Math.Round(position["Y"]!.Value<double>(), 1),
            ["z"] = Math.Round(position["Z"]!.Value<double>(), 1),
        }
        : null;

    private static JObject Text(Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, string table, string? key)
    {
        var result = new JObject();
        if (key == null) return result;
        foreach (var language in TextExporter.Languages)
        {
            if (!texts[language][table].TryGetValue(key, out var raw)) continue;
            var plain = TextExporter.Plain(raw, texts[language]).Trim();
            if (plain.Length > 0 && plain is not ("-" or "???")) result[language] = plain;
        }
        return result;
    }

    /// <param name="Human">The character a quest block may name, for a quest target NPC spawner.</param>
    /// <param name="Quest">The quest the spawner's class defaults name, for a quest battle spawner.</param>
    private sealed record Target(string? Human, string? Quest, JObject Names, double X, double Y, double Z);

    private static List<Target> ReadTargets(DefaultFileProvider provider, JObject? world,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, Blueprints blueprints)
    {
        var result = new List<Target>();
        if (world == null) return result;
        var humans = GameTables.LoadRows(provider, HumanParamTable);
        foreach (var property in world.Properties().Where(p => p.Name.Contains("NPCSpawner", StringComparison.OrdinalIgnoreCase)))
        {
            var isTarget = property.Name.StartsWith("BP_QuestTargetNPCSpawner", StringComparison.OrdinalIgnoreCase);
            var defaults = blueprints.DefaultsOfClass(property.Name);
            var quest = GameTables.Name(defaults?["QuestId"]?["RowName"]);
            if (!isTarget && quest == null) continue;
            foreach (var actor in ((JArray)property.Value).OfType<JObject>())
            {
                var human = RowName(actor["props"]?["HumanName"]) ?? RowName(defaults?["HumanName"]);
                var names = new JObject();
                if (human != null)
                {
                    var key = GameTables.Name(humans.GetValue(human, StringComparison.OrdinalIgnoreCase)?["OverrideNameTextID"]) ?? "NAME_" + human;
                    names = Text(texts, "uniqueNpcText", key);
                    if (names.Count == 0) names = Text(texts, "humanName", key);
                }
                result.Add(new Target(isTarget ? human : null, quest, names,
                    actor["x"]!.Value<double>(), actor["y"]!.Value<double>(), actor["z"]?.Value<double>() ?? 0));
            }
        }
        return result;
    }

    private static JArray ReadGivers(DefaultFileProvider provider, Blueprints blueprints, JObject? world,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, HashSet<string> knownQuests, List<string> warnings)
    {
        var talkRows = GameTables.LoadRows(provider, TalkFlowTable);
        var uniques = GameTables.LoadRows(provider, UniqueNpcTable);
        var graphCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // unique NPC name -> where the level places it: an NPC spawner, or a talkable object that stands for it (TalkFlowId)
        var spots = new SortedDictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in world?.Properties() ?? [])
        {
            var places = property.Name.StartsWith("BP_MonoNPCSpawner", StringComparison.OrdinalIgnoreCase) ||
                         property.Name.Contains("TalkableLevelObject", StringComparison.OrdinalIgnoreCase);
            if (!places || property.Name.Contains("BossBase", StringComparison.OrdinalIgnoreCase)) continue;
            var defaults = blueprints.DefaultsOfClass(property.Name);
            var classUnique = RowName(defaults?["UniqueName"]) ?? GameTables.Name(defaults?["TalkFlowId"]);
            foreach (var actor in ((JArray)property.Value).OfType<JObject>())
            {
                if ((RowName(actor["props"]?["UniqueName"]) ?? classUnique) is not { } unique) continue;
                if (!spots.TryGetValue(unique, out var list)) spots[unique] = list = [];
                list.Add(Spot(actor));
            }
        }

        var uniqueNames = talkRows.Properties().Select(p => p.Name).Concat(uniques.Properties().Select(p => p.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
        var result = new JArray();
        var unplaced = new List<string>();
        foreach (var unique in uniqueNames)
        {
            var graph = ToPackagePath(talkRows.GetValue(unique, StringComparison.OrdinalIgnoreCase)?["SoftTalkFlowAsset"]?["AssetPathName"]?.Value<string>());
            var row = uniques.GetValue(unique, StringComparison.OrdinalIgnoreCase) as JObject;
            if (graph == null && GameTables.Name(row?["TalkBPClass"]) is { } talk)
            {
                graph = new[] { "FABP_" + talk, "FABP_U_" + talk }.Select(n => TalkGraphFolder + n)
                    .FirstOrDefault(p => provider.Files.ContainsKey(p + ".uasset"));
            }
            if (graph == null) continue;
            var quests = Ordered(provider, graph, graphCache, warnings, 0).Where(knownQuests.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (quests.Count == 0) continue;

            var key = GameTables.Name(row?["NameTextID"]);
            var names = Text(texts, "uniqueNpcText", key);
            if (names.Count == 0) names = Text(texts, "humanName", key);
            if (names.Count == 0) names = Text(texts, "uniqueNpcText", unique);
            if (names.Count == 0) names = Text(texts, "humanName", "NAME_" + (GameTables.Name(row?["CharacterID"]) ?? unique));
            var found = spots.TryGetValue(unique, out var list) ? list : [];
            if (found.Count == 0) unplaced.Add($"{unique} ({string.Join(", ", quests)})");
            result.Add(new JObject
            {
                ["unique"] = unique,
                ["names"] = names,
                ["quests"] = new JArray(quests),
                ["spots"] = new JArray(found),
            });
        }
        warnings.Add($"quests: givers without a spawner in the level: {unplaced.Count}: {string.Join("; ", unplaced)}");
        return result;
    }

    private static JObject Spot(JObject actor) => new()
    {
        ["x"] = actor["x"]!.DeepClone(), ["y"] = actor["y"]!.DeepClone(), ["z"] = actor["z"]?.DeepClone() ?? 0,
    };

    /// <summary>The quest ids of the order-quest nodes of a talk graph and of the graphs it runs.</summary>
    private static List<string> Ordered(DefaultFileProvider provider, string package, Dictionary<string, List<string>> cache,
        List<string> warnings, int depth)
    {
        if (cache.TryGetValue(package, out var cached)) return cached;
        var result = cache[package] = [];
        if (!provider.Files.ContainsKey(package + ".uasset")) return result;
        try
        {
            var exports = JArray.Parse(JsonConvert.SerializeObject(provider.LoadPackage(package + ".uasset").GetExports().ToList()));
            foreach (var export in exports.OfType<JObject>())
            {
                if (export["Type"]?.Value<string>()?.StartsWith(OrderNode, StringComparison.Ordinal) == true &&
                    GameTables.Name(export["Properties"]?["QuestId"]?["RowName"]) is { } quest)
                    result.Add(quest);
            }
            if (depth < 3)
            {
                foreach (var sub in Blueprints.StringsOf(exports)
                             .Where(s => s.Contains("/FABP_", StringComparison.Ordinal)).Select(ToPackagePath)
                             .Where(p => p != null && !string.Equals(p, package, StringComparison.OrdinalIgnoreCase))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                    result.AddRange(Ordered(provider, sub!, cache, warnings, depth + 1));
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"quests: talk graph {package} could not be read: {ex.Message}");
        }
        return result;
    }

    /// <summary><c>/Game/Pal/X.X_C</c> or <c>Pal/Content/Pal/X.0</c> -> <c>Pal/Content/Pal/X</c>; null for anything else.</summary>
    private static string? ToPackagePath(string? objectPath)
    {
        if (string.IsNullOrEmpty(objectPath) || objectPath == "None") return null;
        var path = objectPath.StartsWith("/Game/", StringComparison.Ordinal) ? "Pal/Content/" + objectPath["/Game/".Length..] : objectPath;
        if (!path.StartsWith("Pal/Content/", StringComparison.Ordinal)) return null;
        var dot = path.LastIndexOf('.');
        return dot > 0 ? path[..dot] : path;
    }

    private static string? RowName(JToken? token) => GameTables.Name(token is JObject o ? o["Key"] : token);

    /// <summary>Reads Blueprint packages once: class defaults (merged along the parents) and every string value they hold.</summary>
    private sealed class Blueprints(DefaultFileProvider provider, List<string> warnings)
    {
        private readonly Dictionary<string, JArray?> _exports = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string>? _byName;

        public string? PathOfClass(string className)
        {
            _byName ??= provider.Files.Keys
                .Where(k => k.StartsWith("Pal/Content/Pal/Blueprint/", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .GroupBy(k => Path.GetFileNameWithoutExtension(k), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()[..^".uasset".Length], StringComparer.OrdinalIgnoreCase);
            return _byName.GetValueOrDefault(className.EndsWith("_C", StringComparison.Ordinal) ? className[..^2] : className);
        }

        public JObject? DefaultsOfClass(string className) => PathOfClass(className) is { } path ? Defaults(path) : null;

        public JObject? Defaults(string package, int depth = 0)
        {
            if (Load(package) is not { } exports) return null;
            var merged = new JObject();
            if (depth < 8 && Parent(exports) is { } parent && Defaults(parent, depth + 1) is { } inherited) merged.Merge(inherited);
            var own = exports.OfType<JObject>()
                .FirstOrDefault(e => e["Name"]?.Value<string>()?.StartsWith("Default__", StringComparison.Ordinal) == true)?["Properties"] as JObject;
            if (own != null)
            {
                // A property the child sets replaces the parent's (lists are not appended).
                foreach (var property in own.Properties()) merged[property.Name] = property.Value.DeepClone();
            }
            return merged;
        }

        public IReadOnlyList<string> Strings(string package) => Load(package) is { } exports ? StringsOf(exports) : [];

        public static List<string> StringsOf(JToken token) => token.SelectTokens("$..*")
            .Where(t => t.Type == JTokenType.String).Select(t => t.Value<string>()!).ToList();

        private static string? Parent(JArray exports) => ToPackagePath(exports.OfType<JObject>()
            .Where(e => e["Type"]?.Value<string>() == "BlueprintGeneratedClass")
            .Select(e => e["Super"]?["ObjectPath"]?.Value<string>()).FirstOrDefault(p => p != null));

        private JArray? Load(string package)
        {
            if (_exports.TryGetValue(package, out var cached)) return cached;
            JArray? result = null;
            var script = provider.ReadScriptData;
            try
            {
                if (provider.Files.ContainsKey(package + ".uasset"))
                {
                    // The text keys a block reads are constants of its bytecode.
                    provider.ReadScriptData = true;
                    result = JArray.Parse(JsonConvert.SerializeObject(provider.LoadPackage(package + ".uasset").GetExports().ToList()));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"quests: Blueprint {package} could not be read: {ex.Message}");
            }
            finally
            {
                provider.ReadScriptData = script;
            }
            return _exports[package] = result;
        }
    }
}
