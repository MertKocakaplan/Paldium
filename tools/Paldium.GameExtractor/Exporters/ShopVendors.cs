using CUE4Parse.FileProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// T137: finds which NPC sells which shop and where, from game data only:
/// <c>DT_PalHumanParameter.BPClass</c> -> <c>DT_PalBPClass</c> -> the NPC Blueprint (its <c>BP_PalShopVenderDataComponent_C</c>, inherited along the
/// Blueprint parents) -> the lottery table / Pal shop row it opens. Places come from the NPC spawners of the world scan and <c>DT_PalVisitorNPC</c>.
/// Nothing is guessed: a shop without a found vendor has no vendor and no place.
/// </summary>
public sealed class ShopVendors
{
    private const string BpClassTable = "Pal/Content/Pal/DataTable/Character/DT_PalBPClass";
    private const string HumanParamTable = "Pal/Content/Pal/DataTable/Character/DT_PalHumanParameter_Common";
    private const string UniqueNpcTable = "Pal/Content/Pal/DataTable/Character/DT_UniqueNPC";
    private const string VisitorTable = "Pal/Content/Pal/DataTable/Invader/DT_PalVisitorNPC";
    private const string TalkGraphFolder = "Pal/Content/Pal/Blueprint/FlowGraph/NPCTalkFlow/Graph/";
    private const string SpawnerFolder = "Pal/Content/Pal/Blueprint/Spawner/UniqueNPC/";
    private const string VendorComponent = "BP_PalShopVenderDataComponent_C";
    private const string SimpleLottery = "EPalShopLotteryType::SimpleLottery";

    /// <summary>NPC spawners below this height are inside the dungeon level instances.</summary>
    private const double UndergroundZ = -20000;

    /// <param name="ItemLottery">Row of DT_ItemShopLotteryData the NPC opens, or null.</param>
    /// <param name="PalShop">Row of DT_PalShopCreateData the NPC opens, or null.</param>
    public sealed record Vendor(string HumanId, string NameKey, string? ItemLottery, string? PalShop);

    /// <summary>One spawner of a vendor in the world.</summary>
    public sealed record Spot(double X, double Y, bool Underground);

    private readonly Dictionary<string, Vendor> _vendors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Spot>> _spots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _visitors = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Vendor> All => _vendors.Values;

    public IReadOnlyList<Spot> SpotsOf(string humanId) => _spots.TryGetValue(humanId, out var list) ? list : [];

    /// <summary>True when the NPC can come to the player's base (a DT_PalVisitorNPC row with a weight above zero).</summary>
    public bool IsVisitor(string humanId) => _visitors.Contains(humanId);

    public static ShopVendors Load(DefaultFileProvider provider, Dictionary<string, List<JObject>> worldActors, List<string> warnings)
    {
        var result = new ShopVendors();
        var bpClasses = Rows(provider, BpClassTable);
        var humans = Rows(provider, HumanParamTable);
        var uniques = Rows(provider, UniqueNpcTable);
        var cache = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var (humanId, row) in humans)
        {
            var bpKey = GameTables.Name(row["BPClass"]);
            if (bpKey == null || !bpClasses.TryGetValue(bpKey, out var bpRow)) continue;
            var asset = ToPackagePath(bpRow["BPClass"]?["AssetPathName"]?.Value<string>());
            if (asset == null) continue;
            var props = VendorProperties(provider, asset, cache, warnings, 0);
            if (props == null) continue;

            var itemLottery = Active(props, "itemShopLotteryType") ? RowName(props["itemShopSimpleLotteryTableName"]) : null;
            var palShop = Active(props, "palShopLotteryType") ? RowName(props["palShopSimpleLotteryTableName"]) : null;
            if (itemLottery == null && palShop == null) continue;

            var nameKey = GameTables.Name(row["OverrideNameTextID"]) ?? "NAME_" + humanId;
            result._vendors[humanId] = new Vendor(humanId, nameKey, itemLottery, palShop);
        }

        result.ReadSpots(provider, worldActors, uniques, warnings);

        foreach (var (_, row) in Rows(provider, VisitorTable))
        {
            var humanId = GameTables.Name(row["CharactorID_A"]);
            if (humanId != null && (row["Weight"]?.Value<double>() ?? 0) > 0) result._visitors.Add(humanId);
        }
        return result;
    }

    private void ReadSpots(DefaultFileProvider provider, Dictionary<string, List<JObject>> worldActors,
        Dictionary<string, JObject> uniques, List<string> warnings)
    {
        foreach (var (cls, actors) in worldActors)
        {
            // Quest and boss spawners place the same characters for a fight or a quest step, not as a shop.
            if (!cls.StartsWith("BP_MonoNPCSpawner", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("Quest", StringComparison.OrdinalIgnoreCase) || cls.Contains("BossBase", StringComparison.OrdinalIgnoreCase)) continue;

            string? classUnique = null;
            if (!cls.Equals("BP_MonoNPCSpawner_C", StringComparison.OrdinalIgnoreCase) &&
                !cls.Equals("BP_MonoNPCSpawner_Unique_C", StringComparison.OrdinalIgnoreCase))
                classUnique = ClassDefaultUniqueName(provider, cls, warnings);

            foreach (var actor in actors)
            {
                var unique = RowName(actor["props"]?["UniqueName"]) ?? classUnique;
                var human = RowName(actor["props"]?["HumanName"]);
                if (unique != null && uniques.TryGetValue(unique, out var row))
                {
                    if (!TalkOpensShop(provider, GameTables.Name(row["TalkBPClass"]), warnings)) continue;
                    human = GameTables.Name(row["CharacterID"]) ?? human;
                }
                if (human == null || !_vendors.ContainsKey(human)) continue;

                if (!_spots.TryGetValue(human, out var list)) _spots[human] = list = [];
                list.Add(new Spot(
                    Math.Round(actor["x"]!.Value<double>(), 1),
                    Math.Round(actor["y"]!.Value<double>(), 1),
                    (actor["z"]?.Value<double>() ?? 0) < UndergroundZ));
                if (unique != null && uniques.TryGetValue(unique, out var named) && GameTables.Name(named["NameTextID"]) is { } textId)
                    _vendors[human] = _vendors[human] with { NameKey = textId };
            }
        }
    }

    /// <summary>
    /// A unique NPC with its own conversation graph sells only when that graph (or the common graph it uses) has a shop node;
    /// without a graph of its own the vendor component of its Blueprint decides.
    /// </summary>
    private static bool TalkOpensShop(DefaultFileProvider provider, string? talk, List<string> warnings)
    {
        if (talk == null) return true;
        foreach (var name in new[] { "FABP_" + talk, "FABP_U_" + talk })
        {
            var path = TalkGraphFolder + name + ".uasset";
            if (!provider.Files.ContainsKey(path)) continue;
            try
            {
                var json = JsonConvert.SerializeObject(provider.LoadPackage(path).GetExports().ToList());
                return json.Contains("OpenItemShop", StringComparison.Ordinal) || json.Contains("OpenPalShop", StringComparison.Ordinal) ||
                       json.Contains("FABP_Common", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                warnings.Add($"shops: conversation graph {name} could not be read: {ex.Message}");
                return false;
            }
        }
        return true;
    }

    internal static string? ClassDefaultUniqueName(DefaultFileProvider provider, string className, List<string> warnings)
    {
        var path = SpawnerFolder + className[..^2] + ".uasset";
        if (!provider.Files.ContainsKey(path)) return null;
        try
        {
            return Exports(provider, path)
                .Where(e => e["Name"]?.Value<string>()?.StartsWith("Default__", StringComparison.Ordinal) == true)
                .Select(e => RowName(e["Properties"]?["UniqueName"]))
                .FirstOrDefault(n => n != null);
        }
        catch (Exception ex)
        {
            warnings.Add($"shops: spawner {className} could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>The vendor component values of a Blueprint, the parents' values first; null when no Blueprint in the chain has the component.</summary>
    private static JObject? VendorProperties(DefaultFileProvider provider, string package, Dictionary<string, JObject> cache,
        List<string> warnings, int depth)
    {
        if (cache.TryGetValue(package, out var cached)) return cached.Count == 0 ? null : cached;
        var merged = new JObject();
        try
        {
            var exports = Exports(provider, package + ".uasset");
            var parent = ToPackagePath(exports
                .Where(e => e["Type"]?.Value<string>() == "BlueprintGeneratedClass")
                .Select(e => e["Super"]?["ObjectPath"]?.Value<string>())
                .FirstOrDefault(p => p != null));
            if (parent != null && depth < 8 && VendorProperties(provider, parent, cache, warnings, depth + 1) is { } inherited)
                merged.Merge(inherited);
            foreach (var component in exports.Where(e => e["Type"]?.Value<string>() == VendorComponent))
            {
                // The marker keeps an empty override apart from "no component at all".
                merged["_component"] = true;
                if (component["Properties"] is JObject own) merged.Merge(own);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"shops: Blueprint {package} could not be read: {ex.Message}");
        }
        cache[package] = merged;
        return merged.Count == 0 ? null : merged;
    }

    private static List<JObject> Exports(DefaultFileProvider provider, string path) =>
        JArray.Parse(JsonConvert.SerializeObject(provider.LoadPackage(path).GetExports().ToList())).OfType<JObject>().ToList();

    /// <summary><c>/Game/Pal/X.X_C</c> or <c>Pal/Content/Pal/X.0</c> -> <c>Pal/Content/Pal/X</c>; null for engine classes.</summary>
    private static string? ToPackagePath(string? objectPath)
    {
        if (string.IsNullOrEmpty(objectPath) || objectPath == "None") return null;
        var path = objectPath.StartsWith("/Game/", StringComparison.Ordinal) ? "Pal/Content/" + objectPath["/Game/".Length..] : objectPath;
        if (!path.StartsWith("Pal/Content/", StringComparison.Ordinal)) return null;
        var dot = path.LastIndexOf('.');
        return dot > 0 ? path[..dot] : path;
    }

    private static bool Active(JObject props, string typeProperty) => props[typeProperty]?.Value<string>() == SimpleLottery;

    /// <summary>A row reference is written either as a name or as <c>{ "Key": name }</c>.</summary>
    private static string? RowName(JToken? token) => GameTables.Name(token is JObject o ? o["Key"] : token);

    private static Dictionary<string, JObject> Rows(DefaultFileProvider provider, string table) =>
        GameTables.LoadRows(provider, table).Properties().ToDictionary(p => p.Name, p => (JObject)p.Value, StringComparer.OrdinalIgnoreCase);
}
