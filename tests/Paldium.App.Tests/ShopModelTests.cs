using Paldium.App.Pages.GuidesParts;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.App.Tests;

public class ShopModelTests
{
    private static readonly IReadOnlyList<ShopInfo> Shops = PlannerStateServiceTests.GameDataForTests.Shops;

    private static ShopInfo Shop(string id) => Shops.Single(s => s.Id == id);

    private static LocalizedText Text(string en, string? tr = null) =>
        new(new Dictionary<string, string> { ["en"] = en, ["tr"] = tr ?? en }, en);

    private static ShopProduct Product(string id, int price, string? group = null, double chance = 1) =>
        new(id, Text(id), null, price, 1, 0, false, group, chance);

    private static ShopInfo Synthetic(string id, string kind, string place, string currency, IReadOnlyList<ShopPoolEntry> pool,
        params ShopProduct[] products) =>
        new(id, kind, Text("Seller " + id), new ShopPlace(place, Text(""), []), new ShopCurrency(currency, Text(currency), null), null,
            pool, products, 0, []);

    [Theory]
    [InlineData("alev organı")]
    [InlineData("Alev Organı")]
    [InlineData("flame organ")]
    [InlineData("ALEV ORGANI")]
    public void SearchingAnItem_InEitherLanguage_FindsTheSameShops(string query)
    {
        var item = Assert.Single(ShopModel.SearchItems(ShopModel.Items(Shops, "tr"), query));

        Assert.Equal("FireOrgan", item.ItemId);
        Assert.Equal(Shops.Where(s => s.Products.Any(p => p.ItemId == "FireOrgan")).Select(s => s.Id).Order(),
            item.Offers.Select(o => o.Shop.Id).Distinct().Order());
        Assert.All(item.Offers, o => Assert.True(o.Product.Price > 0));
    }

    [Fact]
    public void EveryShopIsInOneGroup_ByWhatItSellsAndWhereItIs()
    {
        Assert.Equal(ShopGroup.Village, ShopModel.GroupOf(Shop("Village_Shop_1")));
        Assert.Equal(ShopGroup.Token, ShopModel.GroupOf(Shop("Medal_Shop_1")));
        Assert.Equal(ShopGroup.Token, ShopModel.GroupOf(Shop("Arena_Shop_1")));
        Assert.Equal(ShopGroup.Pal, ShopModel.GroupOf(Shop("Dark_01")));
        Assert.Equal(ShopGroup.Wandering, ShopModel.GroupOf(Shop("Wander_Shop_1")));
        Assert.Equal(ShopGroup.Dungeon, ShopModel.GroupOf(Shop("Dungeon_Shop_01")));
        Assert.Equal(ShopGroup.Unknown, ShopModel.GroupOf(Shop("Vagrant_Trader_1_1")));

        var grouped = ShopModel.Grouped(Shops);
        Assert.Equal(Shops.Count, grouped.Sum(g => g.Shops.Count));
        Assert.Equal(grouped.Select(g => g.Group).Order(), grouped.Select(g => g.Group)); // Village first, Unknown last
        Assert.All(grouped, g => Assert.NotEmpty(g.Shops));
    }

    [Fact]
    public void SearchingAVendorOrAPlace_FindsTheShop()
    {
        Assert.Contains(ShopModel.SearchShops(Shops, "karaborsaci"), s => s.Id == "Dark_01");
        Assert.Contains(ShopModel.SearchShops(Shops, "küçük yerleşim"), s => s.Id == "Village_Shop_1");
        Assert.Empty(ShopModel.SearchShops(Shops, "xyzxyz"));
        Assert.Empty(ShopModel.SearchShops(Shops, "  "));
    }

    [Fact]
    public void ThePriceRange_IsGivenPerCurrency()
    {
        var a = Synthetic("a", "item", "village", "Money", [], Product("Arrow", 1500));
        var b = Synthetic("b", "item", "village", "Money", [], Product("Arrow", 1200));
        var c = Synthetic("c", "item", "region", "DogCoin", [], Product("Arrow", 12));

        var ranges = ShopModel.PriceRanges(Assert.Single(ShopModel.Items([a, b, c], "en")).Offers);

        Assert.Equal(
            [("Money", 1200, 1500), ("DogCoin", 12, 12)],
            ranges.Select(r => (r.Currency.ItemId, r.Min, r.Max)));
    }

    [Fact]
    public void ALotteryShop_ShowsItsGroupsWithTheirChance_AndAPlainShopOneList()
    {
        var lottery = Synthetic("l", "item", "visitor", "Money",
            [new ShopPoolEntry("l_1", 100, 0.5), new ShopPoolEntry("l_2", 100, 0.5)],
            Product("Arrow", 10, "l_1", 0.5), Product("Wheat", 70, "l_2", 0.5), Product("Bread", 90, "l_2", 0.5));

        var groups = ShopModel.PoolGroups(lottery);

        Assert.Equal([("l_1", 0.5, 1), ("l_2", 0.5, 2)], groups.Select(g => (g.Group!, g.Chance, g.Products.Count)));
        var plain = Assert.Single(ShopModel.PoolGroups(Shop("Village_Shop_1")));
        Assert.Null(plain.Group);
        Assert.Equal(1.0, plain.Chance);
    }

    [Fact]
    public void Products_SortByNameAndByPrice_BothWays()
    {
        var products = new[] { Product("Bread", 90), Product("Arrow", 10), Product("Wheat", 70) };

        Assert.Equal(["Arrow", "Bread", "Wheat"], ShopModel.Sort(products, ShopModel.SortName, false, "en").Select(p => p.ItemId));
        Assert.Equal(["Bread", "Wheat", "Arrow"], ShopModel.Sort(products, ShopModel.SortPrice, true, "en").Select(p => p.ItemId));
    }

    [Fact]
    public void AShopWithMapPoints_LinksToItsFirstMarker()
    {
        var shop = Shop("Village_Shop_1");
        var point = shop.Place.MapPoints[0];

        Assert.Equal(Paldium.Core.Map.ShopMapIds.PointId(shop.Id, point.X, point.Y), ShopModel.MapPointId(shop));
        Assert.Null(ShopModel.MapPointId(Shop("Wander_Shop_1")));
    }

    [Fact]
    public void PlaceNames_AreListedOnce()
    {
        var names = ShopModel.PlaceNames(Shop("Bounty_Shop_1"), "tr");

        Assert.Equal(names.Distinct(), names);
        Assert.Contains("Küçük Yerleşim", names);
    }
}
