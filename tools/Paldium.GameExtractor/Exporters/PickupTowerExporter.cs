using CUE4Parse.FileProvider;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// T143: what lies in the ancient towers (<c>BP_LevelObject_ItemPickupTower</c>): each tower names a row of <c>DT_ItemPickupDataTable</c>
/// (up to three items). A schematic's product is the recipe that needs it (<c>DT_ItemRecipeDataTable.UnlockItemID</c>).
/// Output: <c>pickups.json</c>.
/// </summary>
public static class PickupTowerExporter
{
    private const string PickupTable = "Pal/Content/Pal/DataTable/Item/DT_ItemPickupDataTable";
    private const string RecipeTable = "Pal/Content/Pal/DataTable/Item/DT_ItemRecipeDataTable";

    public static JObject Export(DefaultFileProvider provider, ItemCatalog items, List<string> warnings)
    {
        var unlocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in GameTables.LoadRows(provider, RecipeTable).Properties())
        {
            if (GameTables.Name(row.Value["UnlockItemID"]) is { } schematic && GameTables.Name(row.Value["Product_Id"]) is { } product)
                unlocks.TryAdd(schematic, product);
        }

        var rows = new JObject();
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in GameTables.LoadRows(provider, PickupTable).Properties())
        {
            var list = new JArray();
            for (var index = 1; index <= 3; index++)
            {
                var id = GameTables.Name(row.Value[$"Item_{index:00}_Id"]);
                var count = row.Value[$"Item_{index:00}_Num"]?.Value<int>() ?? 0;
                if (id == null || count <= 0) continue;
                used.Add(id);
                if (unlocks.TryGetValue(id, out var product)) used.Add(product);
                list.Add(new JObject { ["id"] = id, ["count"] = count });
            }
            if (list.Count > 0) rows[row.Name] = list;
        }

        var table = new JObject();
        foreach (var id in used)
        {
            var entry = new JObject { ["names"] = items.Names(id), ["icon"] = items.Icon(id) };
            if (unlocks.TryGetValue(id, out var product)) entry["unlocks"] = product;
            table[id] = entry;
        }

        warnings.Add($"pickups: {rows.Count} rows, {table.Count} items, {table.Properties().Count(p => p.Value["unlocks"] != null)} schematics with a product.");
        return new JObject
        {
            ["source"] = "DT_ItemPickupDataTable (ancient towers: ItemPickupRowName); DT_ItemRecipeDataTable.UnlockItemID",
            ["rows"] = rows,
            ["items"] = table,
        };
    }
}
