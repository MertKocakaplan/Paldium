using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// T128: the technology tree (<c>DT_TechnologyRecipeUnlock</c>, 588 rows): level, points, ancient flag, prerequisites and what each technology unlocks.
/// Output: <c>technologies.json</c> and the icons in <c>icons/items/</c> and <c>icons/buildings/</c>.
/// Names come from <c>DT_TechnologyNameText_Common</c> (often a reference tag to an item or building name), descriptions from the technology, building or item
/// description tables (the row's <c>Description</c> key is in one of the three). Rows without a name are not in the game's menu and are left out.
/// </summary>
public static class TechnologyExporter
{
    private const string BuildIconTable = "Pal/Content/Pal/DataTable/MapObject/Building/DT_BuildObjectIconDataTable";
    private const string ItemIconTable = "Pal/Content/Pal/DataTable/Item/DT_ItemIconDataTable";
    private const string LabTable = "Pal/Content/Pal/DataTable/Lab/DT_LabResearchDataTable";
    private static readonly string[] DescriptionTables = ["DT_TechnologyDescText_Common", "DT_BuildObjectDescText_Common", "DT_ItemDescriptionText_Common"];

    public static JObject Export(DefaultFileProvider provider, string outFolder,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, List<string> warnings)
    {
        var rows = GameTables.LoadRows(provider, SpeciesExporter.TechnologyTable);
        var itemRows = ToCaseInsensitiveMap(GameTables.LoadRows(provider, SpeciesExporter.ItemTable));
        var itemIcons = ToCaseInsensitiveMap(GameTables.LoadRows(provider, ItemIconTable));
        var buildIcons = ToCaseInsensitiveMap(GameTables.LoadRows(provider, BuildIconTable));
        var labRows = ToCaseInsensitiveMap(GameTables.LoadRows(provider, LabTable));

        // Tables only this export needs (kept out of texts.json): description tables and the lab research names.
        var extra = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
        foreach (var language in TextExporter.Languages)
        {
            var tables = new Dictionary<string, Dictionary<string, string>>();
            foreach (var name in DescriptionTables.Append("DT_LabResearchText"))
            {
                try { tables[name] = GameTables.LoadTexts(provider, language, name); }
                catch (FileNotFoundException) { warnings.Add($"technologies: text table {name} not found for {language}."); tables[name] = []; }
            }
            extra[language] = tables;
        }

        var icons = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Icon(string kind, string key)
        {
            var table = kind == "buildings" ? buildIcons : itemIcons;
            if (!table.TryGetValue(key, out var entry)) return null;
            var actualKey = entry.Key;
            var cacheKey = kind + "/" + actualKey;
            if (icons.TryGetValue(cacheKey, out var cached)) return cached;
            var asset = kind == "buildings"
                ? entry.Value["SoftIcon"]?["AssetPathName"]?.Value<string>()
                : entry.Value["Icon"]?["AssetPathName"]?.Value<string>();
            string? path = null;
            if (!string.IsNullOrEmpty(asset) && asset != "None")
            {
                var folder = Path.Combine(outFolder, "icons", kind);
                Directory.CreateDirectory(folder);
                if (RanchExporter.SaveIcon(provider, asset, Path.Combine(folder, actualKey + ".png"), warnings)) path = $"icons/{kind}/{actualKey}.png";
            }
            icons[cacheKey] = path;
            return path;
        }

        string? ItemIcon(string itemId)
        {
            var iconKey = (itemRows.TryGetValue(itemId, out var item) ? GameTables.Name(item.Value["IconName"]) : null) ?? itemId;
            return itemIcons.ContainsKey(iconKey) ? Icon("items", iconKey) : itemIcons.ContainsKey(itemId) ? Icon("items", itemId) : null;
        }

        JObject Localized(Func<string, string?> pick)
        {
            var result = new JObject();
            foreach (var language in TextExporter.Languages)
                if (pick(language) is { Length: > 0 } text) result[language] = text;
            return result;
        }

        string? Text(string language, string table, string key) =>
            (texts[language].TryGetValue(table, out var t) && t.TryGetValue(key, out var v)) ? v
            : (extra[language].TryGetValue(table, out var e) && e.TryGetValue(key, out var w)) ? w : null;

        // The tables a language's reference tags resolve in (the common ones plus nothing extra: tags point to Pals, items, buildings, UI terms).
        // An item's name key is its OverrideName when it has one (GrapplingGun -> ITEM_NAME_GrapplingGun_1): reference tags and unlock names use that.
        var englishTexts = texts;
        texts = TextExporter.Languages.ToDictionary(l => l, l =>
        {
            var copy = new Dictionary<string, Dictionary<string, string>>(englishTexts[l], StringComparer.Ordinal);
            var names = new Dictionary<string, string>(englishTexts[l]["itemName"], StringComparer.OrdinalIgnoreCase);
            foreach (var item in itemRows.Values)
                if (GameTables.Name(item.Value["OverrideName"]) is { } overrideKey && names.TryGetValue(overrideKey, out var overrideText)) names["ITEM_NAME_" + item.Key] = overrideText;
            copy["itemName"] = names;
            return copy;
        });

        var anyUnresolved = 0;
        var technologies = new JArray();
        var index = 0;
        foreach (var row in rows.Properties())
        {
            var p = (JObject)row.Value;
            var nameKey = GameTables.Name(p["Name"]);
            var rawEnglishName = nameKey == null ? null : Text("en", "technologyName", nameKey);
            if (rawEnglishName == null) { warnings.Add($"technologies: {row.Name} has no name and is left out."); continue; }

            var name = Localized(l => Text(l, "technologyName", nameKey!) is { } n ? TextExporter.Plain(n, texts[l]) : null);
            var descriptionKey = GameTables.Name(p["Description"]);
            var description = Localized(l =>
            {
                if (descriptionKey == null) return null;   // (the Turkish texts mark game terms with |word|; the bars are dropped)
                foreach (var table in DescriptionTables)
                    if (extra[l][table].TryGetValue(descriptionKey, out var d)) return TextExporter.Plain(d, texts[l]).Replace("|", "");
                return null;
            });
            if (descriptionKey != null && description.Count == 0) warnings.Add($"technologies: {row.Name}: no description text for {descriptionKey}.");

            // Icon: the name's reference tag says whether the technology is a building (map object) or an item; look there first.
            var iconName = GameTables.Name(p["IconName"]);
            string? icon = null;
            if (iconName != null)
            {
                var building = rawEnglishName.Contains("<mapObjectName", StringComparison.Ordinal);
                icon = building
                    ? (buildIcons.ContainsKey(iconName) ? Icon("buildings", iconName) : itemIcons.ContainsKey(iconName) ? Icon("items", iconName) : null)
                    : (itemIcons.ContainsKey(iconName) ? Icon("items", iconName) : buildIcons.ContainsKey(iconName) ? Icon("buildings", iconName) : null);
            }

            var unlocks = new JArray();
            foreach (var id in p["UnlockBuildObjects"]?.Values<string>() ?? [])
            {
                if (string.IsNullOrEmpty(id) || id == "None") continue;
                var unlockName = Localized(l => Text(l, "mapObjectName", "MAPOBJECT_NAME_" + id) is { } n ? TextExporter.Plain(n, texts[l]) : null);
                if (unlockName.Count == 0) { warnings.Add($"technologies: {row.Name}: building {id} has no name and is left out."); continue; }
                unlocks.Add(new JObject { ["kind"] = "build", ["id"] = id, ["name"] = unlockName, ["icon"] = buildIcons.ContainsKey(id) ? Icon("buildings", id) : null });
            }

            foreach (var id in p["UnlockItemRecipes"]?.Values<string>() ?? [])
            {
                if (string.IsNullOrEmpty(id) || id == "None") continue;
                var unlockName = Localized(l => Text(l, "itemName", "ITEM_NAME_" + id) is { } n ? TextExporter.Plain(n, texts[l]) : null);
                if (unlockName.Count == 0) { warnings.Add($"technologies: {row.Name}: item {id} has no name and is left out."); anyUnresolved++; continue; }
                unlocks.Add(new JObject { ["kind"] = "item", ["id"] = id, ["name"] = unlockName, ["icon"] = itemRows.ContainsKey(id) ? ItemIcon(id) : null });
            }

            JObject? requires = null;
            var requiredTechnology = GameTables.Name(p["RequireTechnology"]);
            var boss = GameTables.Enum(p["RequireDefeatTowerBoss"]);
            var research = GameTables.Name(p["RequireResearchId"]);
            if (requiredTechnology != null || boss != null || research != null)
            {
                requires = new JObject();
                if (requiredTechnology != null) requires["technologyId"] = requiredTechnology;
                if (boss != null)
                {
                    requires["towerBoss"] = new JObject
                    {
                        ["type"] = boss,
                        ["name"] = Localized(l => Text(l, "uiCommon", "BOSS_BATTLE_NAME_" + boss) is { } n ? n.Trim() : null),
                    };
                }

                if (research != null)
                {
                    var textId = labRows.TryGetValue(research, out var lab) ? GameTables.Name(lab.Value["TextId"]) : null;
                    requires["research"] = new JObject
                    {
                        ["id"] = research,
                        ["name"] = Localized(l => textId != null && Text(l, "DT_LabResearchText", textId) is { } n ? n.Trim() : null),
                    };
                }
            }

            technologies.Add(new JObject
            {
                ["id"] = row.Name,
                ["index"] = index++,
                ["level"] = p["LevelCap"],
                ["cost"] = p["Cost"],
                ["ancient"] = p["IsBossTechnology"] ?? false,
                ["name"] = name,
                ["description"] = description,
                ["icon"] = icon,
                ["requires"] = requires,
                ["unlocks"] = unlocks,
            });
        }

        warnings.Add($"technologies: {technologies.Count} technologies, {technologies.Sum(t => t["unlocks"]!.Count())} unlocks, {icons.Values.Count(i => i != null)} icons ({anyUnresolved} item recipes without a name).");
        return new JObject
        {
            ["source"] = "DT_TechnologyRecipeUnlock (LevelCap, Cost, IsBossTechnology, Require*, Unlock*); texts DT_TechnologyNameText_Common, description tables, BOSS_BATTLE_NAME_*, DT_LabResearchText",
            ["technologies"] = technologies,
        };
    }

    private static Dictionary<string, (string Key, JToken Value)> ToCaseInsensitiveMap(JObject table)
    {
        var map = new Dictionary<string, (string Key, JToken Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in table.Properties())
            map.TryAdd(prop.Name, (prop.Name, prop.Value));
        return map;
    }
}
