using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// T126: the Pal Effigy ("relic") enhancements of the player: <c>DT_PlayerStatusRankMasterDataTable</c> (per type and rank: effigies needed and the total effect).
/// Output: <c>relics.json</c> and the effigy item icons in <c>icons/items/</c>.
/// The game has no table that names a type's effigy or its text, so the links are made by position and checked by the text:
/// the 13 types appear in the table in the order of the statue's menu (<c>BUILDUP_PLAYER_STATUS_NN</c> / <c>_DESC_NN</c> in DT_UI_Common_Text_Common, NN = 00..12) and
/// the effigy items are <c>Relic</c> (type 0, the Lifmunk effigy) and <c>Relic_01</c> .. <c>Relic_12</c> in the same order; every text matches its type (swimming for
/// SwimSpeed, climbing for ClimbSpeed, movement for MoveSpeed ...).
/// </summary>
public static class RelicExporter
{
    public const string Table = "Pal/Content/Pal/DataTable/Player/DT_PlayerStatusRankMasterDataTable";
    private const string IconTable = "Pal/Content/Pal/DataTable/Item/DT_ItemIconDataTable";

    public static JObject Export(DefaultFileProvider provider, string outFolder,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, List<string> warnings)
    {
        var rows = GameTables.LoadRows(provider, Table);
        var itemRows = GameTables.LoadRows(provider, SpeciesExporter.ItemTable);
        var iconRows = GameTables.LoadRows(provider, IconTable);

        // Types in the order of their first row (the row names are 1..N, ordered by type and rank).
        var byType = new List<(string Type, List<JObject> Ranks)>();
        foreach (var row in rows.Properties().OrderBy(r => int.TryParse(r.Name, out var n) ? n : int.MaxValue))
        {
            var type = GameTables.Enum(row.Value["RelicType"]) ?? "None";
            var entry = byType.FirstOrDefault(t => t.Type == type);
            if (entry.Ranks == null) { entry = (type, []); byType.Add(entry); }
            entry.Ranks.Add((JObject)row.Value);
        }

        var iconFolder = Path.Combine(outFolder, "icons", "items");
        Directory.CreateDirectory(iconFolder);
        var types = new JArray();
        for (var index = 0; index < byType.Count; index++)
        {
            var (type, ranks) = byType[index];
            var itemId = index == 0 ? "Relic" : $"Relic_{index:00}";
            var names = new JObject();
            var itemNames = new JObject();
            var descriptions = new JObject();
            foreach (var language in TextExporter.Languages)
            {
                if (texts[language]["uiCommon"].TryGetValue($"BUILDUP_PLAYER_STATUS_{index:00}", out var name)) names[language] = name.Trim();
                if (texts[language]["uiCommon"].TryGetValue($"BUILDUP_PLAYER_STATUS_DESC_{index:00}", out var description)) descriptions[language] = TextExporter.Plain(description, texts[language]);
                if (texts[language]["itemName"].TryGetValue("ITEM_NAME_" + itemId, out var itemName)) itemNames[language] = itemName.Trim();
            }

            string? icon = null;
            var iconName = GameTables.Name(itemRows[itemId]?["IconName"]) ?? itemId;
            var iconAsset = (iconRows[iconName] ?? iconRows[itemId])?["Icon"]?["AssetPathName"]?.Value<string>();
            if (iconAsset != null && RanchExporter.SaveIcon(provider, iconAsset, Path.Combine(iconFolder, itemId + ".png"), warnings)) icon = $"icons/items/{itemId}.png";
            else warnings.Add($"relics: no icon for item {itemId}.");

            types.Add(new JObject
            {
                ["type"] = type,
                ["index"] = index,
                ["itemId"] = itemId,
                ["itemNames"] = itemNames,
                ["names"] = names,
                ["descriptions"] = descriptions,
                ["icon"] = icon,
                ["ranks"] = new JArray(ranks.OrderBy(r => r["Rank"]!.Value<int>()).Select(r => new JObject
                {
                    ["rank"] = r["Rank"],
                    ["required"] = r["RequiredRelicNum"],
                    ["effect"] = r["EffectRate"],
                })),
            });
        }

        warnings.Add($"relics: {types.Count} types, {types.Sum(t => t["ranks"]!.Count())} ranks.");
        return new JObject
        {
            ["source"] = "DT_PlayerStatusRankMasterDataTable (RelicType, Rank, RequiredRelicNum, EffectRate); texts BUILDUP_PLAYER_STATUS_NN; items Relic, Relic_01..12",
            ["types"] = types,
        };
    }
}
