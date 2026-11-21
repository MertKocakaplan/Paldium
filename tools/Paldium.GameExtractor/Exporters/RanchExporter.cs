using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// What a Pal drops when it is assigned to the Ranch (T108). The data is in three places:
/// <list type="bullet">
/// <item>the Pal's Blueprint (<c>DT_PalBPClass</c> row, e.g. <c>BP_SheepBall</c>): <c>SpawnItem.FieldLotteryNameByRank</c> maps a rank (1 to 10) to a lottery name;</item>
/// <item><c>DT_ItemLotteryDataTable</c>: rows with that <c>FieldName</c> (<c>CharacterSpawnItem_Egg_3</c>): slot, weight, item, min and max count;</item>
/// <item><c>DT_ItemIconDataTable</c> and the texture it points at: the item icon.</item>
/// </list>
/// Output: <c>ranch.json</c> and the icons in <c>icons/items/</c> (64 px PNG).
/// </summary>
public static class RanchExporter
{
    private const string BpTable = "Pal/Content/Pal/DataTable/Character/DT_PalBPClass";
    private const string LotteryTable = "Pal/Content/Pal/DataTable/Item/DT_ItemLotteryDataTable";
    private const string ItemTable = "Pal/Content/Pal/DataTable/Item/DT_ItemDataTable";
    private const string IconTable = "Pal/Content/Pal/DataTable/Item/DT_ItemIconDataTable";
    public const int IconSize = 64;

    public static JObject Export(
        DefaultFileProvider provider, string outFolder,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, List<string> warnings)
    {
        var bpRows = GameTables.LoadRows(provider, BpTable);
        var lotteryRows = GameTables.LoadRows(provider, LotteryTable);

        // lottery name -> rows
        var lotteries = new Dictionary<string, List<JObject>>(StringComparer.Ordinal);
        foreach (var row in lotteryRows.Properties())
        {
            var name = row.Value["FieldName"]?.Value<string>();
            if (name == null || !name.StartsWith("CharacterSpawnItem", StringComparison.Ordinal)) continue;
            if (!lotteries.TryGetValue(name, out var list)) lotteries[name] = list = [];
            list.Add((JObject)row.Value);
        }

        var pals = new JObject();
        var usedLotteries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var bp in bpRows.Properties())
        {
            if (bp.Name.StartsWith("BOSS_", StringComparison.Ordinal) || bp.Name.StartsWith("GYM_", StringComparison.Ordinal)) continue;
            var asset = bp.Value["BPClass"]?["AssetPathName"]?.Value<string>();
            if (string.IsNullOrEmpty(asset) || asset == "None") continue;
            var byRank = ReadSpawnItem(provider, asset, warnings);
            if (byRank == null || byRank.Count == 0) continue;
            foreach (var value in byRank.Properties()) usedLotteries.Add(value.Value.Value<string>()!);
            pals[bp.Name] = new JObject { ["byRank"] = byRank };
        }

        var lottery = new JObject();
        var itemIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in usedLotteries)
        {
            if (!lotteries.TryGetValue(name, out var list)) { warnings.Add($"ranch: lottery {name} has no rows."); continue; }
            var rows = new JArray();
            foreach (var r in list)
            {
                var item = GameTables.Name(r["StaticItemId"]);
                if (item == null) continue;
                itemIds.Add(item);
                rows.Add(new JObject
                {
                    ["slot"] = r["SlotNo"], ["weight"] = r["WeightInSlot"], ["item"] = item, ["min"] = r["MinNum"], ["max"] = r["MaxNum"],
                });
            }

            lottery[name] = rows;
        }

        var items = new JObject();
        var iconRows = GameTables.LoadRows(provider, IconTable);
        var itemRows = GameTables.LoadRows(provider, ItemTable);
        var iconFolder = Path.Combine(outFolder, "icons", "items");
        Directory.CreateDirectory(iconFolder);
        foreach (var id in itemIds)
        {
            var names = new JObject();
            foreach (var language in TextExporter.Languages)
                if (texts[language]["itemName"].TryGetValue("ITEM_NAME_" + id, out var text)) names[language] = text.Trim();
            var entry = new JObject { ["names"] = names };
            // The icon row is named like the item's IconName (usually the item id itself).
            var iconName = GameTables.Name(itemRows[id]?["IconName"]) ?? id;
            var iconAsset = (iconRows[iconName] ?? iconRows[id])?["Icon"]?["AssetPathName"]?.Value<string>();
            if (iconAsset != null && SaveIcon(provider, iconAsset, Path.Combine(iconFolder, id + ".png"), warnings)) entry["icon"] = $"icons/items/{id}.png";
            else warnings.Add($"ranch: no icon for item {id}.");
            items[id] = entry;
        }

        warnings.Add($"ranch: {pals.Count} Pals, {lottery.Count} lotteries, {items.Count} items.");
        return new JObject
        {
            ["source"] = "DT_PalBPClass -> Pal Blueprint StaticCharacterParameter SpawnItem.FieldLotteryNameByRank -> DT_ItemLotteryDataTable (FieldName CharacterSpawnItem_*)",
            ["pals"] = pals, ["lotteries"] = lottery, ["items"] = items,
        };
    }

    private static string ToGamePath(string assetPath)
    {
        // "/Game/Pal/Blueprint/X/BP_X.BP_X_C" -> "Pal/Content/Pal/Blueprint/X/BP_X"
        var path = assetPath.Split('.')[0];
        if (path.StartsWith("/Game/", StringComparison.Ordinal)) path = path["/Game/".Length..];
        return "Pal/Content/" + path;
    }

    private static JObject? ReadSpawnItem(DefaultFileProvider provider, string assetPath, List<string> warnings)
    {
        try
        {
            var package = provider.LoadPackage(ToGamePath(assetPath));
            foreach (var export in package.GetExports())
            {
                if (export.Name.StartsWith("Default__", StringComparison.Ordinal) is false && export.GetType().Name is "UBlueprintGeneratedClass") continue;
                var json = JObject.Parse(JsonConvert.SerializeObject(export));
                var spawn = json["Properties"]?["SpawnItem"]?["FieldLotteryNameByRank"] as JArray;
                if (spawn == null) continue;
                var result = new JObject();
                foreach (var pair in spawn)
                {
                    var rank = pair["Key"]?.ToString();
                    var name = pair["Value"]?["Key"]?.ToString();
                    if (rank != null && !string.IsNullOrEmpty(name) && name != "None") result[rank] = name;
                }

                return result;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"ranch: {assetPath}: {ex.Message}");
        }

        return null;
    }

    internal static bool SaveIcon(DefaultFileProvider provider, string assetPath, string target, List<string> warnings)
    {
        try
        {
            var texture = provider.LoadPackageObject<UTexture2D>(ToGamePath(assetPath) + "." + Path.GetFileName(assetPath.Split('.')[0]));
            var mip = texture.GetFirstMip();
            if (mip?.BulkData?.Data == null) { warnings.Add($"ranch icon {assetPath}: no pixel data."); return false; }
            var rgba = TextureDecoder.ToRgba(texture.Format, mip.BulkData.Data, mip.SizeX, mip.SizeY);
            if (rgba == null) { warnings.Add($"ranch icon {assetPath}: format {texture.Format} ({mip.SizeX}x{mip.SizeY}) is not read."); return false; }
            var (small, w, h) = TextureDecoder.Downscale(rgba, mip.SizeX, mip.SizeY, IconSize);
            File.WriteAllBytes(target, PngWriter.Write(small, w, h));
            return true;
        }
        catch (Exception ex)
        {
            warnings.Add($"ranch icon {assetPath}: {ex.Message}");
            return false;
        }
    }
}
