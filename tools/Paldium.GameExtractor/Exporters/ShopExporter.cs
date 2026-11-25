using System.Globalization;
using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// T137: Exports all item shops and Pal shops (<c>shops.json</c>) plus product and currency icons (<c>icons/items/</c>).
/// Test shop rows (<c>TestShop_*</c>, <c>Skin_Shop_Test</c>, <c>Test_*</c>) are excluded. Vendors and places come from <see cref="ShopVendors"/>.
/// Buy prices follow <c>OverridePrice &gt; 0 ? OverridePrice : DT_ItemDataTable.Price</c>; currencies come from <c>DT_ItemShopSettingData_Common</c>.
/// </summary>
public static class ShopExporter
{
    private const string ItemShopCreateTable = "Pal/Content/Pal/DataTable/ItemShop/DT_ItemShopCreateData";
    private const string ItemShopLotteryTable = "Pal/Content/Pal/DataTable/ItemShop/DT_ItemShopLotteryData";
    private const string ItemShopSettingTable = "Pal/Content/Pal/DataTable/ItemShop/DT_ItemShopSettingData_Common";
    private const string PalShopCreateTable = "Pal/Content/Pal/DataTable/PalShop/DT_PalShopCreateData";
    private const string AreaTable = "Pal/Content/Pal/DataTable/WorldMapAreaData/DT_WorldMapAreaData";

    /// <summary>A shop spot gets the name of the closest region marker within this distance (world units, 1 km).</summary>
    private const double RegionNameDistance = 100000;

    private static bool IsTestRow(string id) =>
        id.StartsWith("Test", StringComparison.OrdinalIgnoreCase) || id.EndsWith("_Test", StringComparison.OrdinalIgnoreCase);

    public static JObject Export(
        DefaultFileProvider provider,
        string outFolder,
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts,
        List<string> warnings,
        string? worldActorsPath = null)
    {
        var itemShopCreate = ToMap(GameTables.LoadRows(provider, ItemShopCreateTable));
        var itemShopLottery = ToMap(GameTables.LoadRows(provider, ItemShopLotteryTable));
        var itemShopSetting = ToMap(GameTables.LoadRows(provider, ItemShopSettingTable));
        var palShopCreate = ToMap(GameTables.LoadRows(provider, PalShopCreateTable));
        var items = new ItemCatalog(provider, outFolder, texts, warnings);
        var areaRows = ToMap(GameTables.LoadRows(provider, AreaTable));

        var worldActors = LoadWorldActors(worldActorsPath);
        var regionCenters = BuildRegionCenters(worldActors);
        JObject BuildCurrency(string currencyItemId)
        {
            return new JObject
            {
                ["itemId"] = currencyItemId,
                ["names"] = items.Names(currencyItemId),
                ["icon"] = items.Icon(currencyItemId),
            };
        }

        // Preload currencies (Money + DT_ItemShopSettingData_Common special currencies)
        var currencyByGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currencyObjects = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase)
        {
            ["Money"] = BuildCurrency("Money"),
        };
        foreach (var (groupId, row) in itemShopSetting)
        {
            var cur = GameTables.Name(row["CurrencyItemID"]) ?? "Money";
            currencyByGroup[groupId] = cur;
            if (!currencyObjects.ContainsKey(cur))
                currencyObjects[cur] = BuildCurrency(cur);
        }

        var vendors = ShopVendors.Load(provider, worldActors, warnings);

        JObject VendorNames(IReadOnlyList<ShopVendors.Vendor> sellers)
        {
            var names = new JObject();
            // The NPC met most often names the shop; boss and arena copies of a vendor come last.
            var main = sellers.OrderByDescending(v => vendors.SpotsOf(v.HumanId).Count)
                .ThenByDescending(v => vendors.IsVisitor(v.HumanId))
                .ThenBy(v => v.HumanId.StartsWith("BOSS_", StringComparison.OrdinalIgnoreCase) || v.HumanId.StartsWith("Arena_", StringComparison.OrdinalIgnoreCase))
                .ThenBy(v => v.HumanId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (main == null) return names;
            foreach (var lang in TextExporter.Languages)
            {
                foreach (var table in new[] { "uniqueNpcText", "humanName" })
                {
                    if (texts[lang].TryGetValue(table, out var rows) && rows.TryGetValue(main.NameKey, out var text) && IsUsable(text))
                    {
                        names[lang] = TextExporter.Plain(text, texts[lang]).Trim();
                        break;
                    }
                }
            }
            return names;
        }

        (string? Key, JObject Names) NearestRegion(double x, double y)
        {
            var best = regionCenters
                .Select(r => (r.Key, Distance: Math.Sqrt(Math.Pow(r.Value.X - x, 2) + Math.Pow(r.Value.Y - y, 2))))
                .Where(r => r.Distance <= RegionNameDistance && areaRows.ContainsKey(r.Key))
                .OrderBy(r => r.Distance)
                .Select(r => r.Key)
                .FirstOrDefault();
            var names = new JObject();
            if (best == null || areaRows[best]["MsgID"]?.Value<string>() is not { } msgId) return (null, names);
            foreach (var lang in TextExporter.Languages)
            {
                if (texts[lang]["worldMap"].TryGetValue(msgId, out var text) && IsUsable(text))
                    names[lang] = TextExporter.Plain(text, texts[lang]).Trim();
            }
            return (best, names);
        }

        JObject Place(IReadOnlyList<ShopVendors.Vendor> sellers)
        {
            var spots = sellers.SelectMany(v => vendors.SpotsOf(v.HumanId)).ToList();
            var points = new JArray();
            var regionKeys = new List<string>();
            var pointNames = new List<JObject>();
            foreach (var spot in spots.Where(s => !s.Underground).DistinctBy(s => (s.X, s.Y)).OrderBy(s => s.X).ThenBy(s => s.Y))
            {
                var (key, names) = NearestRegion(spot.X, spot.Y);
                var point = new JObject { ["x"] = spot.X, ["y"] = spot.Y };
                if (names.Count > 0) point["names"] = names;
                points.Add(point);
                pointNames.Add(names);
                if (key != null) regionKeys.Add(key);
            }

            var kind = points.Count > 0
                ? regionKeys.Any(k => k.Contains("Village", StringComparison.OrdinalIgnoreCase)) ? "village"
                    : regionKeys.Any(k => k.Equals("Arena", StringComparison.OrdinalIgnoreCase)) ? "arena" : "region"
                : spots.Count > 0 ? "dungeon"
                : sellers.Any(v => vendors.IsVisitor(v.HumanId)) ? "visitor"
                : "unknown";
            // One name for the whole shop only when every spot is in the same region; otherwise each point carries its own.
            var oneName = pointNames.Select(n => n.ToString(Formatting.None)).Distinct().Count() == 1;
            return new JObject
            {
                ["kind"] = kind,
                ["names"] = oneName ? pointNames[0].DeepClone() : new JObject(),
                ["mapPoints"] = points,
            };
        }

        JArray BuildProducts(string createGroupId)
        {
            var products = new JArray();
            if (!itemShopCreate.TryGetValue(createGroupId, out var groupRow)) return products;
            foreach (var prod in (groupRow["productDataArray"] as JArray ?? []).OfType<JObject>())
            {
                var itemId = GameTables.Name(prod["StaticItemId"]);
                if (itemId == null) continue;
                var itemRow = items.Row(itemId);
                var overridePrice = prod["OverridePrice"]?.Value<int>() ?? 0;
                var basePrice = itemRow?["Price"]?.Value<int>() ?? 0;
                var price = overridePrice > 0 ? overridePrice : basePrice;
                var pType = GameTables.Enum(prod["ProductType"]) ?? "Normal";

                products.Add(new JObject
                {
                    ["itemId"] = itemId,
                    ["names"] = items.Names(itemId),
                    ["icon"] = items.Icon(itemId),
                    ["price"] = price,
                    ["perPurchase"] = prod["ProductNum"]?.Value<int>() ?? 1,
                    ["stock"] = prod["Stock"]?.Value<int>() ?? 0,
                    ["oncePerPlayer"] = string.Equals(pType, "OnlyPurchaseOne", StringComparison.OrdinalIgnoreCase),
                });
            }
            return products;
        }

        var shops = new JArray();

        JObject ItemShop(string id, IReadOnlyList<(string Group, double Weight)> pool, IReadOnlyList<ShopVendors.Vendor> sellers)
        {
            var total = pool.Sum(p => p.Weight);
            var poolArr = new JArray();
            var products = new JArray();
            foreach (var (group, weight) in pool)
            {
                var chance = total > 0 ? Math.Round(weight / total, 4) : 1.0;
                poolArr.Add(new JObject { ["group"] = group, ["weight"] = weight, ["chance"] = chance });
                foreach (var product in BuildProducts(group).OfType<JObject>())
                {
                    product["poolGroup"] = group;
                    product["chance"] = chance;
                    products.Add(product);
                }
            }
            var currency = currencyByGroup.TryGetValue(pool[0].Group, out var c) ? c : "Money";
            return new JObject
            {
                ["id"] = id,
                ["kind"] = "item",
                ["vendor"] = new JObject { ["names"] = VendorNames(sellers) },
                ["place"] = Place(sellers),
                ["currency"] = currencyObjects[currency],
                ["pool"] = poolArr,
                ["products"] = products,
            };
        }

        // 1. The lottery rows the vendors open: one shop per row, named after its group when it has only one.
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sellersByLottery = vendors.All.Where(v => v.ItemLottery != null)
            .GroupBy(v => v.ItemLottery!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ShopVendors.Vendor>)g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (lotteryId, lotteryRow) in itemShopLottery)
        {
            if (!sellersByLottery.TryGetValue(lotteryId, out var sellers)) continue;
            var pool = (lotteryRow["lotteryDataArray"] as JArray ?? [])
                .OfType<JObject>()
                .Select(e => (Group: e["ShopGroupName"]?.Value<string>() ?? "", Weight: e["Weight"]?.Value<double>() ?? 0))
                .Where(e => itemShopCreate.ContainsKey(e.Group) && !IsTestRow(e.Group))
                .ToList();
            if (pool.Count == 0 || (pool.Count == 1 && covered.Contains(pool[0].Group))) continue;
            foreach (var (group, _) in pool) covered.Add(group);
            shops.Add(ItemShop(pool.Count == 1 ? pool[0].Group : lotteryId, pool, sellers));
        }

        // 2. Shop groups no found vendor opens: listed with an unknown place.
        foreach (var groupId in itemShopCreate.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (IsTestRow(groupId) || covered.Contains(groupId)) continue;
            warnings.Add($"shops: no vendor opens the shop group '{groupId}'.");
            shops.Add(ItemShop(groupId, [(groupId, 100)], []));
        }

        // 3. Pal shops.
        foreach (var (shopId, row) in palShopCreate)
        {
            if (IsTestRow(shopId)) continue;
            var sellers = vendors.All.Where(v => string.Equals(v.PalShop, shopId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sellers.Count == 0) warnings.Add($"shops: no vendor opens the Pal shop '{shopId}'.");
            var min = row["MinCharacterLevel"]?.Value<int>() ?? 1;
            var max = row["MaxCharacterLevel"]?.Value<int>() ?? 1;
            var pals = new JArray();
            foreach (var entry in row["CharacterIDArray"] as JArray ?? [])
            {
                if (GameTables.Name(entry["Key"]) is { } palId)
                    pals.Add(new JObject { ["palId"] = palId, ["levelMin"] = min, ["levelMax"] = max });
            }

            shops.Add(new JObject
            {
                ["id"] = shopId,
                ["kind"] = "pal",
                ["vendor"] = new JObject { ["names"] = VendorNames(sellers) },
                ["place"] = Place(sellers),
                ["currency"] = currencyObjects["Money"],
                ["characterNum"] = row["CharacterNum"]?.Value<int>() ?? 0,
                ["pals"] = pals,
            });
        }

        warnings.Add($"shops: {shops.Count} shops exported ({currencyObjects.Count} currencies: {string.Join(", ", currencyObjects.Keys)}).");
        return new JObject
        {
            ["currencies"] = new JObject(currencyObjects.Select(kv => new JProperty(kv.Key, kv.Value))),
            ["shops"] = shops,
        };
    }

    private static Dictionary<string, List<JObject>> LoadWorldActors(string? path)
    {
        var dict = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return dict;
        var root = JObject.Parse(File.ReadAllText(path));
        foreach (var prop in root.Properties())
        {
            if (prop.Value is JArray arr)
                dict[prop.Name] = arr.OfType<JObject>().ToList();
        }
        return dict;
    }

    private static Dictionary<string, (double X, double Y)> BuildRegionCenters(Dictionary<string, List<JObject>> worldActors)
    {
        var centers = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in new[] { "BP_PalRegionTriggerBox_C", "BP_PalRegionTriggerSphere_C" })
        {
            if (!worldActors.TryGetValue(cls, out var arr)) continue;
            foreach (var a in arr)
            {
                var key = a["props"]?["AreaName"]?["Key"]?.Value<string>();
                if (string.IsNullOrEmpty(key) || centers.ContainsKey(key)) continue;
                centers[key] = (a["x"]!.Value<double>(), a["y"]!.Value<double>());
            }
        }
        return centers;
    }

    private static Dictionary<string, JObject> ToMap(JObject rows) =>
        rows.Properties().ToDictionary(p => p.Name, p => (JObject)p.Value, StringComparer.OrdinalIgnoreCase);

    private static bool IsUsable(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim() is not ("-" or "???") && !text.Trim().EndsWith(" Text", StringComparison.Ordinal);
}
