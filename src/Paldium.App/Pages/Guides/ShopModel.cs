using System.Globalization;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Map;
using Paldium.Core.Queries;

namespace Paldium.App.Pages.GuidesParts;

/// <summary>The groups of the vendor list, in the order they are shown.</summary>
public enum ShopGroup
{
    Village,
    Pal,
    Wandering,
    Token,
    Dungeon,
    Unknown,
}

public sealed record ShopOffer(ShopInfo Shop, ShopProduct Product);

/// <summary>An item some shop sells, with every offer of it.</summary>
public sealed record ShopItem(string ItemId, LocalizedText Names, string? IconPath, IReadOnlyList<ShopOffer> Offers);

public sealed record ShopPriceRange(ShopCurrency Currency, int Min, int Max);

/// <summary>The products of one draw of a shop: <see cref="Group"/> is null for a shop that always sells the same goods.</summary>
public sealed record ShopPoolGroup(string? Group, double Chance, IReadOnlyList<ShopProduct> Products);

/// <summary>
/// The Merchants reference table (screens/guides.md "Tüccarlar: kim ne satar", T137): who sells what, at what price and where, from
/// <see cref="IGameData.Shops"/>. Pure functions over the data bundle, tested without the page.
/// </summary>
public static class ShopModel
{
    public const string SortName = "name";
    public const string SortPrice = "price";
    private const string Money = "Money";

    /// <summary>The search reads both languages of every name, whatever the UI language is.</summary>
    private static readonly string[] Languages = ["en", "tr"];

    public static ShopGroup GroupOf(ShopInfo shop) =>
        shop.Kind == "pal" ? ShopGroup.Pal
        : shop.Currency.ItemId != Money ? ShopGroup.Token
        : shop.Place.Kind switch
        {
            "visitor" or "wandering" => ShopGroup.Wandering,
            "dungeon" => ShopGroup.Dungeon,
            "unknown" => ShopGroup.Unknown,
            _ => ShopGroup.Village,
        };

    /// <summary>The shops by group, in the data's order inside a group; empty groups are left out.</summary>
    public static IReadOnlyList<(ShopGroup Group, IReadOnlyList<ShopInfo> Shops)> Grouped(IEnumerable<ShopInfo> shops) =>
        shops.GroupBy(GroupOf).OrderBy(g => g.Key).Select(g => (g.Key, (IReadOnlyList<ShopInfo>)g.ToList())).ToList();

    /// <summary>Every item any shop sells, by item name.</summary>
    public static IReadOnlyList<ShopItem> Items(IEnumerable<ShopInfo> shops, string lang)
    {
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo(lang), ignoreCase: true);
        return shops.SelectMany(s => s.Products.Select(p => new ShopOffer(s, p)))
            .GroupBy(o => o.Product.ItemId, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First().Product;
                return new ShopItem(g.Key, first.Names, first.IconPath, g.ToList());
            })
            .OrderBy(i => i.Names.Get(lang), comparer)
            .ToList();
    }

    public static IReadOnlyList<ShopItem> SearchItems(IEnumerable<ShopItem> items, string query)
    {
        var q = TextFold.Fold(query).Trim();
        return q.Length == 0 ? items.ToList() : items.Where(i => Matches(i.Names, q)).ToList();
    }

    /// <summary>The shops whose vendor or place name matches; none for an empty search (the plain list is grouped instead).</summary>
    public static IReadOnlyList<ShopInfo> SearchShops(IEnumerable<ShopInfo> shops, string query)
    {
        var q = TextFold.Fold(query).Trim();
        if (q.Length == 0) return [];
        return shops.Where(s => Matches(s.VendorNames, q) || Matches(s.Place.Names, q) ||
                                s.Place.MapPoints.Any(p => p.Names != null && Matches(p.Names, q))).ToList();
    }

    private static bool Matches(LocalizedText names, string foldedQuery) =>
        Languages.Any(l => names.TryGet(l, out var text) && TextFold.Fold(text).Contains(foldedQuery, StringComparison.Ordinal));

    /// <summary>Lowest and highest price of the offers, one range per currency (gold first).</summary>
    public static IReadOnlyList<ShopPriceRange> PriceRanges(IEnumerable<ShopOffer> offers) =>
        offers.GroupBy(o => o.Shop.Currency.ItemId, StringComparer.Ordinal)
            .OrderBy(g => g.Key == Money ? 0 : 1).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ShopPriceRange(g.First().Shop.Currency, g.Min(o => o.Product.Price), g.Max(o => o.Product.Price)))
            .ToList();

    /// <summary>One entry per draw of a lottery shop, with its chance; one entry without a group for every other shop.</summary>
    public static IReadOnlyList<ShopPoolGroup> PoolGroups(ShopInfo shop)
    {
        if (shop.Pool.Count <= 1) return [new ShopPoolGroup(null, 1.0, shop.Products)];
        return shop.Pool.Select(p => new ShopPoolGroup(p.Group, p.Chance,
            shop.Products.Where(x => x.PoolGroup == p.Group).ToList())).ToList();
    }

    public static IReadOnlyList<ShopProduct> Sort(IEnumerable<ShopProduct> products, string sort, bool descending, string lang)
    {
        var comparer = StringComparer.Create(CultureInfo.GetCultureInfo(lang), ignoreCase: true);
        var ordered = sort == SortPrice
            ? (descending ? products.OrderByDescending(p => p.Price) : products.OrderBy(p => p.Price)).ThenBy(p => p.Names.Get(lang), comparer)
            : descending ? products.OrderByDescending(p => p.Names.Get(lang), comparer) : products.OrderBy(p => p.Names.Get(lang), comparer);
        return ordered.ToList();
    }

    /// <summary>The distinct names of the places the shop stands at (empty for visitors, dungeons and unknown places).</summary>
    public static IReadOnlyList<string> PlaceNames(ShopInfo shop, string lang) =>
        shop.Place.MapPoints.Where(p => p.Names != null).Select(p => p.Names!.Get(lang)).Where(n => n.Length > 0).Distinct().ToList();

    /// <summary>The ancient towers that hold the item (T143); none for the Dog Coins, which lie in every tower.</summary>
    public static IReadOnlyList<MapMarker> Towers(MapData? map, string itemId) =>
        map == null || string.Equals(itemId, "DogCoin", StringComparison.OrdinalIgnoreCase)
            ? []
            : map.Points.Where(p => p.Category == "pickup_tower" && p.Items?.Any(s => string.Equals(s.ItemId, itemId, StringComparison.OrdinalIgnoreCase)) == true).ToList();

    /// <summary>The marker the "show on map" link opens, or null when the shop has no place on the map.</summary>
    public static string? MapPointId(ShopInfo shop) =>
        shop.Place.MapPoints.Count == 0 ? null : ShopMapIds.PointId(shop.Id, shop.Place.MapPoints[0].X, shop.Place.MapPoints[0].Y);
}
