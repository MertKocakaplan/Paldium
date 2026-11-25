using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Map;

namespace Paldium.Core.Tests;

/// <summary>T137/T138 wave 2: the shops of <c>gamedata.json</c> and the markers of <c>map/map-points.json</c>.</summary>
public class MapDataTests
{
    private static readonly Lazy<MapData> Loaded = new(() => MapDataLoader.Load(TestPaths.DataFolder));
    private static readonly Lazy<IGameData> Game = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static MapData Map => Loaded.Value;

    [Fact]
    public void BothProjections_PutTheirCornersOnTheCornersOfThePicture()
    {
        foreach (var projection in new[] { MapProjection.Main, MapProjection.Tree })
        {
            // North up: the world X maximum is the top edge and the world Y minimum the left edge.
            Assert.Equal((0.0, 0.0), projection.ToFraction(projection.MaxX, projection.MinY));
            Assert.Equal((1.0, 1.0), projection.ToFraction(projection.MinX, projection.MaxY));
        }

        Assert.Equal(WorldMapProjection.MinX, MapProjection.Main.MinX);
        Assert.Equal(WorldMapProjection.MaxY, MapProjection.Main.MaxY);
        Assert.Null(MapProjection.Tree.ToMapCoordinates(500000, -600000));
    }

    [Fact]
    public void TheMapBounds_OfTheBundle_AreTheOnesOfTheProjections()
    {
        foreach (var surface in Map.Maps)
        {
            var expected = MapProjection.ForMap(surface.Id);
            Assert.Equal(expected.MinX, surface.Projection.MinX, 1);
            Assert.Equal(expected.MaxX, surface.Projection.MaxX, 1);
            Assert.Equal(expected.MinY, surface.Projection.MinY, 1);
            Assert.Equal(expected.MaxY, surface.Projection.MaxY, 1);
            Assert.True(File.Exists(Path.Combine(TestPaths.DataFolder, "map", surface.Image)), surface.Image);
            Assert.True(File.Exists(Path.Combine(TestPaths.DataFolder, "map", surface.LargeImage)), surface.LargeImage);
        }

        Assert.Equal(["MainMap", "Tree"], Map.Maps.Select(m => m.Id));
    }

    [Fact]
    public void EveryPoint_HasAKnownCategory_AUniqueId_AndLiesInsideItsMap()
    {
        Assert.Equal(Map.Points.Count, Map.Points.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var point in Map.Points)
        {
            Assert.NotNull(Map.TryGetCategory(point.Category));
            var projection = MapProjection.ForMap(point.Map);
            Assert.True(projection.Contains(point.X, point.Y), $"{point.Id} is outside {point.Map}");
            // Game coordinates exist on the main map only; none are made up for the World Tree.
            Assert.Equal(point.Map == "MainMap", point.MapX.HasValue && point.MapY.HasValue);
            if (point.RegionId != null) Assert.Equal(point.Map, Map.TryGetRegion(point.RegionId)!.Map);
        }
    }

    [Fact]
    public void EveryCategory_HasPoints()
    {
        // The predators have no markers of their own: the app draws the wild areas of their base species (T149).
        foreach (var category in Map.Categories.Where(c => c.Id != "predator"))
        {
            Assert.True(Map.Points.Any(p => p.Category == category.Id), "no points: " + category.Id);
            if (category.Icon != null)
                Assert.True(File.Exists(Path.Combine(TestPaths.DataFolder, category.Icon)), "no icon file: " + category.Icon);
        }
    }

    [Fact]
    public void AKnownAlpha_IsAPoint_WithItsPalAndLevel()
    {
        var anubis = Assert.Single(Map.Points, p => p.Category == "alpha" && p.RefId == "Anubis");
        Assert.Equal("pal", anubis.RefKind);
        Assert.Equal("MainMap", anubis.Map);
        Assert.Equal("Lv 55", anubis.Extra);
        Assert.InRange(anubis.X, -167300, -167100);
        Assert.InRange(anubis.Y, 96300, 96600);
    }

    [Fact]
    public void TheWorldTree_HasItsOwnPoints()
    {
        var tree = Map.Points.Where(p => p.Map == "Tree").ToList();
        Assert.Contains(tree, p => p.Category == "fast_travel");
        Assert.All(tree, p => Assert.Null(p.MapX));
        Assert.Contains(Map.Regions, r => r.Map == "Tree");
    }

    [Fact]
    public void FastTravelPoints_AreUniqueAndNamedInBothLanguages()
    {
        var points = Map.Points.Where(p => p.Category == "fast_travel").ToList();
        Assert.InRange(points.Count, 130, 160);
        Assert.All(points, p =>
        {
            Assert.NotNull(p.Names);
            Assert.True(p.Names!.TryGet("en", out _) && p.Names.TryGet("tr", out _), p.Id);
        });
    }

    /// <summary>
    /// The checked categories of game build 25246127 with their marker counts (main map, World Tree). A change means the bundle was
    /// rebuilt from another game build or the extraction changed: check the places again (T138 entry condition) before updating.
    /// </summary>
    [Fact]
    public void TheCategories_AreTheCheckedOnes_WithTheirCounts()
    {
        var expected = new Dictionary<string, (int Main, int Tree)>
        {
            ["fast_travel"] = (137, 15), ["boss_tower"] = (9, 4), ["sealed_realm"] = (18, 0), ["dungeon"] = (157, 0),
            ["merchant"] = (23, 0), ["npc"] = (40, 0), ["quest_giver"] = (59, 2), ["effigy"] = (360, 47), ["pickup_tower"] = (106, 0),
            ["oil_rig"] = (3, 0), ["quest"] = (158, 13), ["hexolite"] = (349, 0), ["journal"] = (55, 9), ["chest"] = (1577, 38),
            ["egg"] = (1786, 30), ["skill_fruit"] = (31, 12), ["alpha"] = (65, 7), ["bounty"] = (33, 0), ["ore"] = (1555, 0),
            ["coal"] = (497, 0), ["sulfur"] = (257, 0), ["quartz"] = (496, 0), ["paldium"] = (1173, 0), ["oil"] = (185, 0),
            ["sky_ore"] = (208, 0), ["worldtree_ore"] = (0, 80), ["watchtower"] = (20, 2), ["predator"] = (0, 0),
        };
        var actual = Map.Categories.ToDictionary(c => c.Id, c => (
            Map.Points.Count(p => p.Category == c.Id && p.Map == "MainMap"),
            Map.Points.Count(p => p.Category == c.Id && p.Map == "Tree")));
        Assert.Equal(expected.OrderBy(e => e.Key, StringComparer.Ordinal), actual.OrderBy(a => a.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void EverySealedRealm_ShowsItsBoss_WhoIsNotAnAlphaPointToo()
    {
        var realms = Map.Points.Where(p => p.Category == "sealed_realm").ToList();
        Assert.All(realms, p =>
        {
            Assert.Equal("pal", p.RefKind);
            Assert.NotNull(Game.Value.Pal(p.RefId!));
            Assert.StartsWith("Lv ", p.Extra);
        });
        var alphas = Map.Points.Where(p => p.Category == "alpha").ToList();
        Assert.All(realms, r => Assert.DoesNotContain(alphas, a => Math.Abs(a.X - r.X) < 500 && Math.Abs(a.Y - r.Y) < 500));
    }

    [Fact]
    public void EveryEffigy_KnowsItsType()
    {
        var types = Game.Value.Relics.Select(r => r.Type).ToHashSet(StringComparer.Ordinal);
        var effigies = Map.Points.Where(p => p.Category == "effigy").ToList();
        Assert.Equal(407, effigies.Count);
        Assert.All(effigies, p => Assert.Contains(p.Extra!, types));
    }

    [Fact]
    public void TheMarkerFile_StaysUnderFourMegabytes() =>
        Assert.True(new FileInfo(Path.Combine(TestPaths.DataFolder, "map", "map-points.json")).Length < 4 * 1024 * 1024);

    // ---- ancient towers (T143) --------------------------------------------------------------------------------

    [Fact]
    public void EveryAncientTower_HasItsOwnPickupRow_AndItsContents()
    {
        var towers = Map.Points.Where(p => p.Category == "pickup_tower").ToList();

        Assert.Equal(106, towers.Count);
        Assert.Equal(106, towers.Select(p => p.Extra).Distinct(StringComparer.Ordinal).Count());
        Assert.All(towers, p =>
        {
            Assert.NotEmpty(p.Items!);
            Assert.Contains(p.Items!, s => s.ItemId == "DogCoin" && s.Count > 0);
            Assert.All(p.Items!, s =>
            {
                var item = Map.TryGetItem(s.ItemId);
                Assert.True(item != null && item.Names.TryGet("en", out _), s.ItemId);
                Assert.True(File.Exists(Path.Combine(TestPaths.DataFolder, item!.Icon!)), s.ItemId);
                if (item.Unlocks != null) Assert.NotNull(Map.TryGetItem(item.Unlocks));
            });
        });
        Assert.Equal(26, towers.Count(p => p.Hint != null));
    }

    [Fact]
    public void TheOldRevolverTower_HoldsItsSchematic_ThatUnlocksTheRevolver()
    {
        var tower = Assert.Single(Map.Points, p => p.Category == "pickup_tower" && p.Extra == "D_OldRevolver");

        Assert.Equal(new[] { new MapStack("Blueprint_OldRevolver_3", 1), new MapStack("DogCoin", 25) }, tower.Items);
        Assert.Equal("OldRevolver_3", Map.TryGetItem("Blueprint_OldRevolver_3")!.Unlocks);
    }

    // ---- quests (T145) ----------------------------------------------------------------------------------------

    [Fact]
    public void TheQuests_AreTheMainAndSideOnes_WithoutHiddenAndTestRows()
    {
        Assert.Equal(57, Map.Quests.Count(q => q.IsMain));
        Assert.Equal(61, Map.Quests.Count(q => !q.IsMain));
        Assert.DoesNotContain(Map.Quests, q => q.Id.StartsWith("Hidden_", StringComparison.Ordinal) || q.Id.StartsWith("Test", StringComparison.Ordinal));
        Assert.All(Map.Quests, q =>
        {
            Assert.True(q.Names.TryGet("en", out _) && q.Names.TryGet("tr", out _), q.Id);
            Assert.All(q.Points, id => Assert.NotNull(Map.TryGetPoint(id)));
            Assert.All(q.Rewards, r => Assert.NotNull(Map.TryGetItem(r.ItemId)));
            // A variable the data has no value for never reaches the page.
            Assert.All(q.Steps, s => Assert.DoesNotContain('{', s.Names.Get("en")));
            Assert.All(q.Steps, s => Assert.InRange(s.Block, 0, q.Blocks - 1));
        });
    }

    [Fact]
    public void ASideQuest_IsGivenByTheNpcWhoseTalkOrdersIt_WithItsRewards()
    {
        var quest = Map.TryGetQuest("Sub_Farmer01")!;

        Assert.Equal("Desperate Farmer", Assert.Single(quest.Givers).Get("en"));
        var giver = Map.TryGetPoint(quest.Points[0])!;
        Assert.Equal("quest_giver", giver.Category);
        Assert.Contains("Sub_Farmer01", giver.Quests!);
        Assert.Equal(240, quest.Exp);
        Assert.Equal(new[] { new MapStack("Fruit__defense_01", 1), new MapStack("PalSphere_Mega", 5), new MapStack("Money", 2000) }, quest.Rewards);
        Assert.Single(quest.Steps);
    }

    [Fact]
    public void EveryQuestMarker_NamesItsQuests_AndEveryQuestPointLinksBack()
    {
        foreach (var point in Map.Points.Where(p => p.Category is "quest" or "quest_giver"))
        {
            Assert.NotEmpty(point.Quests!);
            Assert.All(point.Quests!, id => Assert.Contains(point.Id, Map.TryGetQuest(id)!.Points));
        }

        var sides = Map.Quests.Where(q => !q.IsMain).ToList();
        Assert.True(sides.Count(q => q.Points.Any(id => Map.TryGetPoint(id)!.Category == "quest_giver")) >= 50);
    }

    // ---- watchtowers, tower bosses, journals, predators (T144, T148, T149) ------------------------------------

    [Fact]
    public void EveryWatchtower_IsNamedByTheGame()
    {
        var towers = Map.Points.Where(p => p.Category == "watchtower").ToList();

        Assert.Equal(22, towers.Count);
        Assert.All(towers, p => Assert.True(p.Names != null && p.Names.TryGet("tr", out _), p.Id));
    }

    [Fact]
    public void EveryTowerBoss_HasTheKeyOfTheRecord_AndTheStoryTowersTheirBoss()
    {
        var towers = Map.Points.Where(p => p.Category == "boss_tower").ToList();

        Assert.All(towers, p => Assert.StartsWith("BOSS_BATTLE_NAME_", p.RefId));
        Assert.Equal(towers.Count, towers.Select(p => p.RefId).Distinct().Count());
        Assert.Equal(8, towers.Count(p => p.Boss != null));
        var grass = Map.TryGetPoint("tower:GrassBoss")!;
        Assert.Equal("Rayne Syndicate Tower", grass.Names!.Get("en"));
        Assert.Equal("Zoe & Grizzbolt", grass.Boss!.Get("en"));
        Assert.Equal("Axel & Orserk", Map.TryGetPoint("tower:ElectricBoss")!.Boss!.Get("en"));
        Assert.Equal("Marcus & Faleris", Map.TryGetPoint("tower:DesertBoss")!.Boss!.Get("en"));
    }

    [Fact]
    public void EveryJournal_KnowsItsRowName() =>
        Assert.All(Map.Points.Where(p => p.Category == "journal"), p => Assert.False(string.IsNullOrEmpty(p.Extra), p.Id));

    [Fact]
    public void ThePredators_AreSpeciesOfTheBundle_WithTheGamesName()
    {
        Assert.Equal(43, Map.Predators.Count);
        Assert.All(Map.Predators, p =>
        {
            var pal = Game.Value.Pal(p.PalId);
            Assert.Equal("Rampaging " + pal.Names.Get("en"), p.Names.Get("en"));
        });
    }

    // ---- level spawners (T135) --------------------------------------------------------------------------------

    [Fact]
    public void Suzaku_SpawnsWildInTheDesert_AtLevel40To44_AllDay()
    {
        var wild = Game.Value.Pal("Suzaku").Obtain!.Wild;
        var desert = wild.Where(w => w.LevelMin == 40 && w.LevelMax == 44 && w.Points.Any(p => p.Map == "MainMap")).ToList();
        Assert.NotEmpty(desert);
        Assert.All(desert, w => Assert.False(w.Night));
        Assert.True(desert.SelectMany(w => w.Points).Count() >= 13);
    }

    [Fact]
    public void TheWildPoints_AreNotFewerThanBeforeTheLevelSpawners() =>
        Assert.True(Game.Value.Pals.Sum(p => p.Obtain?.Wild.Sum(w => w.Points.Count) ?? 0) >= 57993);

    // ---- shops (T137) -----------------------------------------------------------------------------------------

    [Fact]
    public void FlameOrgan_IsSoldByAShop_WithAPriceAndACurrency()
    {
        var sellers = Game.Value.Shops.Where(s => s.Products.Any(p => p.ItemId == "FireOrgan")).ToList();
        Assert.NotEmpty(sellers);
        Assert.All(sellers, s =>
        {
            Assert.True(s.Products.First(p => p.ItemId == "FireOrgan").Price > 0);
            Assert.Equal("Money", s.Currency.ItemId);
        });
    }

    [Fact]
    public void TheMedalShop_SellsForDogCoins()
    {
        var shop = Assert.Single(Game.Value.Shops, s => s.Id == "Medal_Shop_1");
        Assert.Equal("DogCoin", shop.Currency.ItemId);
        Assert.NotEmpty(shop.Place.MapPoints);
    }

    [Fact]
    public void NoTestShop_IsListed_AndEveryShopHasGoods()
    {
        Assert.All(Game.Value.Shops, s =>
        {
            Assert.DoesNotContain("Test", s.Id, StringComparison.OrdinalIgnoreCase);
            Assert.True(s.Kind == "pal" ? s.Pals.Count > 0 : s.Products.Count > 0, s.Id);
            Assert.Contains(s.Place.Kind, new[] { "village", "region", "arena", "dungeon", "visitor", "unknown" });
            Assert.Equal(s.Place.Kind is "village" or "region" or "arena", s.Place.MapPoints.Count > 0);
        });
    }

    [Fact]
    public void AShopVendor_ComesFromTheGameData_ForVillageAndPalShops()
    {
        var village = Game.Value.Shops.Single(s => s.Id == "Village_Shop_1");
        Assert.Equal("village", village.Place.Kind);
        Assert.True(village.VendorNames.TryGet("tr", out _));

        var dark = Game.Value.Shops.Single(s => s.Id == "Dark_01");
        Assert.Equal("pal", dark.Kind);
        Assert.NotEmpty(dark.Place.MapPoints);
    }

    [Fact]
    public void EveryShopPlace_HasItsMarker_UnderTheSharedId()
    {
        foreach (var shop in Game.Value.Shops)
        {
            foreach (var place in shop.Place.MapPoints)
            {
                var marker = Map.TryGetPoint(ShopMapIds.PointId(shop.Id, place.X, place.Y));
                Assert.True(marker != null, "no marker for " + shop.Id);
                Assert.Equal("merchant", marker!.Category);
            }
        }
    }

    [Fact]
    public void EveryMerchantPoint_LinksToAListedShop()
    {
        var shops = Game.Value.Shops.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var merchants = Map.Points.Where(p => p.Category == "merchant").ToList();
        Assert.NotEmpty(merchants);
        Assert.All(merchants, p =>
        {
            Assert.Equal("shop", p.RefKind);
            Assert.Contains(p.RefId!, shops);
        });
    }
}
