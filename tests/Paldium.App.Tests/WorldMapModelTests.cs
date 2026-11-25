using Paldium.App.Pages.MapParts;
using Paldium.App.Services;
using Paldium.Core.Domain;
using Paldium.Core.Map;
using Paldium.Core.Owned;

namespace Paldium.App.Tests;

public class WorldMapModelTests
{
    private static readonly Lazy<MapData> Loaded = new(() => MapDataLoader.Load(FindDataFolder()));

    private static MapData Map => Loaded.Value;

    private static string FindDataFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data");
    }

    [Theory]
    [InlineData("main", "MainMap")]
    [InlineData("TREE", "Tree")]
    [InlineData("MainMap", "MainMap")]
    [InlineData(null, null)]
    [InlineData("moon", null)]
    public void TheMapParameter_NamesOneOfTheTwoMaps(string? value, string? expected) =>
        Assert.Equal(expected, WorldMapModel.MapId(value));

    private static IReadOnlyList<MapLayer> Layers => WorldMapModel.Layers(Map, PlannerStateServiceTests.GameDataForTests.Relics);

    [Fact]
    public void WithoutASavedChoice_TheDefaultCategoriesAreOn()
    {
        var on = WorldMapModel.Enabled(null, Layers);

        Assert.Equal(
            new[] { "alpha", "boss_tower", "dungeon", "fast_travel", "merchant", "pickup_tower", "quest_giver", "sealed_realm", "watchtower" },
            on.Order(StringComparer.Ordinal));
        Assert.Empty(WorldMapModel.Enabled([], Layers));
        Assert.Equal(["egg"], WorldMapModel.Enabled(["egg", "gone"], Layers));
    }

    [Fact]
    public void AnEarlierChoice_GetsTheDefaultCategoriesAddedSince_TheLatestDoesNot()
    {
        Assert.Equal(new[] { "egg", "pickup_tower", "quest_giver", "watchtower" }, WorldMapModel.Enabled(["egg"], Layers, savedSet: 0).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "egg", "watchtower" }, WorldMapModel.Enabled(["egg"], Layers, savedSet: 2).Order(StringComparer.Ordinal));
        Assert.Equal(["egg"], WorldMapModel.Enabled(["egg"], Layers, savedSet: WorldMapModel.LayerSet));
    }

    [Fact]
    public void Groups_FollowTheSpecOrder_AndKeepEveryCategory()
    {
        var groups = WorldMapModel.Groups(Map.Categories);

        Assert.Equal(["Locations", "Npc", "Collectibles", "Items", "Enemies", "Quests", "Materials"], groups.Select(g => g.Group));
        Assert.Equal(Map.Categories.Count, groups.Sum(g => g.Categories.Count));
    }

    [Fact]
    public void Counts_AreOfTheShownMap()
    {
        var main = WorldMapModel.Counts(Map.Points, "MainMap");
        var tree = WorldMapModel.Counts(Map.Points, "Tree");

        Assert.Equal(137, main["fast_travel"]);
        Assert.Equal(15, tree["fast_travel"]);
        Assert.Equal(0, tree.GetValueOrDefault("merchant"));
        Assert.Equal(80, tree["worldtree_ore"]);
    }

    [Fact]
    public void APointWithoutAName_IsCalledAfterItsEffigyOrItsCategory()
    {
        var data = PlannerStateServiceTests.GameDataForTests;
        var effigy = Map.Points.First(p => p.Category == "effigy" && p.Extra == "CapturePower");
        var dungeon = Map.Points.First(p => p.Category == "dungeon");

        Assert.Equal("Lifmunk Effigy", WorldMapModel.Title(effigy, "Effigies", data, "en"));
        Assert.Equal("Dungeons", WorldMapModel.Title(dungeon, "Dungeons", data, "en"));
        var alpha = Map.Points.Single(p => p.Category == "alpha" && p.RefId == "Anubis");
        Assert.Equal("Anubis", WorldMapModel.Title(alpha, "Field Boss", data, "en"));
    }

    [Theory]
    [InlineData("Grass_Grade_02", 2)]
    [InlineData("volcanic_grade_01", 1)]
    [InlineData("desert_01", null)]
    [InlineData("Oilrig", null)]
    [InlineData(null, null)]
    public void TheGrade_OfAChestOrEggTag_IsReadOnlyWhereTheTagSaysIt(string? tag, int? grade) =>
        Assert.Equal(grade, WorldMapModel.Grade(tag));

    [Fact]
    public void TheLevelOfAMarker_AndTheLetterOfACategoryWithoutIcon()
    {
        Assert.Equal(55, WorldMapModel.Level("Lv 55"));
        Assert.Null(WorldMapModel.Level("Grass1"));
        Assert.Null(WorldMapModel.Level(null));
        Assert.Equal("Z", WorldMapModel.Letter("zindanlar"));
    }

    [Fact]
    public void Coordinates_UseTheMinusSignAndTheGroupingOfTheLanguage()
    {
        Assert.Equal("(−1.280, −626)", WorldMapModel.Coordinates(-1280, -626, "tr"));
        Assert.Equal("(−1,280, 626)", WorldMapModel.Coordinates(-1280, 626, "en"));
        Assert.Null(WorldMapModel.Coordinates(null, null, "en"));
    }

    [Fact]
    public void SakurajimaIsFound_WhateverTheCase_AndTheIslandByItsTurkishName()
    {
        var data = PlannerStateServiceTests.GameDataForTests;
        foreach (var query in new[] { "Sakurajima", "sakurajima" })
        {
            var first = MapSearchEngine.Search(query, "tr", "MainMap", Map, data)[0];
            Assert.Equal(MapSearchKind.Region, first.Kind);
            Assert.Equal("REGION_Wide_Sakurajima01", first.Id);
        }

        var island = MapSearchEngine.Search("gunes armasi", "tr", "MainMap", Map, data)[0];
        Assert.Equal("New_SmallIsland07", island.Id);
    }

    [Fact]
    public void Search_GroupsFastTravelMerchantsAndPals_AndWorksWithoutTheMarkerFile()
    {
        var data = PlannerStateServiceTests.GameDataForTests;
        var fastTravel = Map.Points.First(p => p.Category == "fast_travel" && p.Map == "Tree");
        var hits = MapSearchEngine.Search(fastTravel.Names!.Get("en"), "en", "MainMap", Map, data);
        Assert.Contains(hits, h => h.PointId == fastTravel.Id && h.MapId == "Tree");

        Assert.Contains(MapSearchEngine.Search("tüccar", "tr", "MainMap", Map, data), h => h.Kind == MapSearchKind.Merchant);
        var pals = MapSearchEngine.Search("anubis", "en", "MainMap", null, data);
        Assert.Equal(MapSearchKind.Pal, Assert.Single(pals, h => h.PalId == "Anubis").Kind);
        Assert.Empty(MapSearchEngine.Search("xyzxyz", "en", "MainMap", Map, data));
    }

    [Fact]
    public void APalSearch_OpensTheMapWhereTheSpeciesSpawnsWild_NotOnlyItsAlpha()
    {
        var data = PlannerStateServiceTests.GameDataForTests;
        // Jormuntide has an Alpha on the main map, but its wild areas are on the World Tree.
        var hit = Assert.Single(MapSearchEngine.Search("jormuntide", "en", "MainMap", null, data), h => h.PalId == "Umihebi");
        Assert.Equal("Tree", hit.MapId);
        // A species with wild areas on both maps stays on the shown one.
        var both = Assert.Single(MapSearchEngine.Search("eikthyrdeer", "en", "Tree", null, data), h => h.PalId == "Deer");
        Assert.Equal("Tree", both.MapId);
    }

    [Fact]
    public void PointIds_AreStableAndUnique_SoALinkKeepsWorking()
    {
        // The ids come from the game's own instance ids, table rows or the shop and its position, never from the order of the file.
        Assert.All(Map.Points.Where(p => p.Category == "alpha"), p => Assert.StartsWith("alpha:", p.Id));
        Assert.All(Map.Points.Where(p => p.Category == "merchant"), p => Assert.StartsWith("shop:", p.Id));
        Assert.All(Map.Points.Where(p => p.Category is "fast_travel" or "effigy" or "dungeon"), p => Assert.DoesNotContain('#', p.Id));
    }

    // ---- effigy types (T142) ----------------------------------------------------------------------------------

    [Fact]
    public void EveryEffigyType_IsItsOwnLayer_WithItsOwnPicture()
    {
        var types = Layers.Where(l => l.Category.Id == "effigy").ToList();

        Assert.Equal(12, types.Count);
        Assert.All(types, l => Assert.NotNull(l.Relic?.IconPath));
        Assert.Equal(12, types.Select(l => l.Relic!.IconPath).Distinct().Count());
        Assert.All(types, l => Assert.True(File.Exists(Path.Combine(FindDataFolder(), l.Relic!.IconPath!)), l.Relic!.IconPath));
        var main = WorldMapModel.Counts(Map.Points, "MainMap");
        var tree = WorldMapModel.Counts(Map.Points, "Tree");
        Assert.Equal(407, types.Sum(l => main.GetValueOrDefault(l.Id) + tree.GetValueOrDefault(l.Id)));
        Assert.Equal(155, main.GetValueOrDefault("effigy:CapturePower") + tree.GetValueOrDefault("effigy:CapturePower"));
    }

    [Fact]
    public void OneTypeCanBeTurnedOff_AndASavedEffigyCategoryMeansEveryType()
    {
        var all = WorldMapModel.Enabled(["effigy"], Layers);
        Assert.Equal(12, all.Count);

        var one = WorldMapModel.Enabled(all.Where(id => id != "effigy:SwimSpeed").ToList(), Layers);
        Assert.Equal(11, one.Count);
        Assert.DoesNotContain("effigy:SwimSpeed", one);
    }

    [Theory]
    [InlineData("CapturePower", "Carbunclo")]
    [InlineData("HungerReduction", "SheepBall")]
    [InlineData("SwimSpeed", "Penguin")]
    [InlineData("StaminaReduction", "PinkCat")]
    [InlineData("ClimbSpeed", "Monkey")]
    public void AnEffigy_ShowsThePalItIsNamedAfter(string type, string palId)
    {
        var data = PlannerStateServiceTests.GameDataForTests;
        var relic = data.Relics.Single(r => r.Type == type);

        Assert.Equal(palId, WorldMapModel.EffigyPal(relic, data.Pals)?.Id, ignoreCase: true);
    }

    [Fact]
    public void EveryEffigyTypeOnTheMap_HasAPal()
    {
        var data = PlannerStateServiceTests.GameDataForTests;
        Assert.All(Layers.Where(l => l.Relic != null), l => Assert.NotNull(WorldMapModel.EffigyPal(l.Relic!, data.Pals)));
    }

    // ---- collected, opened, defeated (T144, T148) -------------------------------------------------------------

    private static WorldProgress Record(IEnumerable<string>? objects = null, IEnumerable<string>? notes = null, IEnumerable<string>? towers = null,
        QuestProgress? quests = null) => new()
    {
        Objects = WorldProgress.Set((objects ?? []).Select(CaptureRecord.ObjectKey)),
        Notes = WorldProgress.Set(notes ?? []),
        Towers = WorldProgress.Set(towers ?? []),
        Quests = quests,
    };

    [Fact]
    public void AMarkerIsDone_WhenTheUserMarkedIt_OrTheRecordSaysSo()
    {
        var effigies = Map.Points.Where(p => p.Category == "effigy").Take(2).ToList();
        var tower = Map.Points.First(p => p.Category == "pickup_tower");
        var fastTravel = Map.Points.First(p => p.Category == "fast_travel");
        var watchtower = Map.Points.First(p => p.Category == "watchtower");
        var journal = Map.Points.First(p => p.Category == "journal");
        var boss = Map.Points.Single(p => p.Id == "tower:GrassBoss");
        var other = Map.Points.First(p => p.Category == "chest");
        // A record spells an object id without dashes; a tower can also be recorded by its row; a journal by its row name.
        var record = Record(
            [effigies[0].Id.Replace("-", "").ToLowerInvariant(), tower.Extra!, fastTravel.Id, watchtower.Id, other.Id],
            [journal.Extra!.ToUpperInvariant()], ["BOSS_BATTLE_NAME_GrassBoss"]);

        var done = WorldMapModel.Done(Map.Points, [effigies[1].Id, other.Id], record);

        Assert.Equal(new[] { effigies[0].Id, effigies[1].Id, tower.Id, fastTravel.Id, watchtower.Id, journal.Id, boss.Id }.Order(), done.Order());
        Assert.True(WorldMapModel.DoneInRecord(boss, record));
        Assert.False(WorldMapModel.DoneInRecord(effigies[1], record));
        Assert.False(WorldMapModel.DoneInRecord(effigies[0], null));
        Assert.Equal(MarkKind.None, WorldMapModel.KindOf(other.Category)); // a chest cannot be marked
        Assert.Equal(2, WorldMapModel.DoneCounts(Map.Points, effigies[0].Map, done)[WorldMapModel.Effigy]);
    }

    [Fact]
    public void Marks_AreKeptPerWorld_AndToggleBack()
    {
        var marks = WorldMapModel.Toggle(new Dictionary<string, IReadOnlyList<string>>(), "world-a", "p1");
        marks = WorldMapModel.Toggle(marks, "world-b", "p2");
        marks = WorldMapModel.Toggle(marks, "world-a", "p3");

        Assert.Equal(["p1", "p3"], WorldMapModel.Marked(marks, "world-a"));
        Assert.Equal(["p2"], WorldMapModel.Marked(marks, "world-b"));
        marks = WorldMapModel.Toggle(marks, "world-b", "p2");
        Assert.False(marks.ContainsKey("world-b"));
        Assert.Empty(WorldMapModel.Marked(marks, "default"));
    }

    [Fact]
    public void TheMarks_SurviveARestartOfTheApp()
    {
        var file = Path.Combine(Path.GetTempPath(), "paldium-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(file);
            store.Update(s => s with { Map = s.Map with { Collected = WorldMapModel.Toggle(s.Map.Collected, "world-a", "p1"), HideCollected = true, Tab = "quests", Pal = "Anubis" } });

            var again = new SettingsStore(file).Current.Map;

            Assert.Equal(["p1"], WorldMapModel.Marked(again.Collected, "world-a"));
            Assert.True(again.HideCollected);
            Assert.Equal(("quests", "Anubis"), (again.Tab, again.Pal));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TheWorldKey_IsThatOfTheFirstSourceThatKnowsItsWorld()
    {
        SourceStatus Source(string id, string? world) => new(id, SourceState.Ok, null, 0, null) { WorldId = world };

        Assert.Equal("default", WorldMapModel.WorldKey([Source("global", null)]));
        var sources = new[] { Source("global", null), Source("save", "W1"), Source("mod", "W1"), Source("other", "W2") };
        Assert.Equal("W1", WorldMapModel.WorldKey(sources));
        Assert.Equal(["save", "mod"], WorldMapModel.SourcesOfWorld(sources, "W1"));
        // A mod file that names no world is read when no source knows one.
        Assert.Equal(["global"], WorldMapModel.SourcesOfWorld(sources, "default"));
    }

    // ---- quests (T145) ----------------------------------------------------------------------------------------

    [Fact]
    public void TheQuestTab_ListsMainAndSideQuests_WithGiverAndRegion()
    {
        var main = WorldMapModel.Quests(Map, main: true, filter: null, "en");
        var side = WorldMapModel.Quests(Map, main: false, filter: null, "en");

        Assert.Equal(57, main.Count);
        Assert.Equal(61, side.Count);
        var farmer = side.Single(r => r.Quest.Id == "Sub_Farmer01");
        Assert.Equal("Desperate Farmer", farmer.Giver);
        Assert.Equal("quest_giver", farmer.Point?.Category);
        Assert.NotNull(farmer.Region);
        Assert.Contains(main, r => r.Point == null); // a quest without a place stays in the list
    }

    [Fact]
    public void TheQuestFilter_LooksAtNamesAndGivers_InBothLanguages()
    {
        Assert.Contains(WorldMapModel.Quests(Map, false, "zoe", "tr"), r => r.Quest.Id == "Sub_Zoe01");
        Assert.Contains(WorldMapModel.Quests(Map, false, "pal'in lutfu", "en"), r => r.Quest.Id == "Sub_Farmer01");
        Assert.Empty(WorldMapModel.Quests(Map, false, "xyzxyz", "en"));
    }

    [Fact]
    public void AQuestsPlaces_ListASpotOnce_TheGiverFirst()
    {
        var places = WorldMapModel.QuestPlaces(Map.TryGetQuest("Sub_Farmer01")!, Map);

        Assert.Equal("quest_giver", Assert.Single(places).Category);
    }

    // ---- quest state (T150) -----------------------------------------------------------------------------------

    private static QuestProgress Quests(string[] done, params (string Id, int Block)[] active) =>
        new(active.ToDictionary(a => a.Id, a => a.Block), WorldProgress.Set(done));

    [Fact]
    public void QuestRows_ShowTheirState_AndCanBeNarrowedToOne()
    {
        var progress = Quests(["Sub_Farmer01"], ("Sub_Zoe02", 1));

        var all = WorldMapModel.Quests(Map, false, null, "en", progress);
        Assert.Equal(QuestStatus.Done, all.Single(r => r.Quest.Id == "Sub_Farmer01").Status);
        var active = all.Single(r => r.Quest.Id == "Sub_Zoe02");
        Assert.Equal((QuestStatus.Active, 2), (active.Status, active.Step));
        Assert.Equal(QuestStatus.NotTaken, all.Single(r => r.Quest.Id == "Sub_Angler01").Status);

        Assert.Equal("Sub_Zoe02", Assert.Single(WorldMapModel.Quests(Map, false, null, "en", progress, QuestFilter.Active)).Quest.Id);
        Assert.Equal(60, WorldMapModel.Quests(Map, false, null, "en", progress, hideDone: true).Count);
        Assert.Equal("Sub_Farmer01", Assert.Single(WorldMapModel.Quests(Map, false, null, "en", progress, QuestFilter.Done, hideDone: true)).Quest.Id);
        // Without a record nothing is filtered away and nothing has a state.
        Assert.All(WorldMapModel.Quests(Map, false, null, "en", null, QuestFilter.Done, hideDone: true), r => Assert.Equal(QuestStatus.Unknown, r.Status));
    }

    [Fact]
    public void TheCurrentStep_IsTheLastStepTextAtOrBeforeTheBlock()
    {
        var zoe = Map.TryGetQuest("Sub_Zoe02")!;

        Assert.Equal(zoe.Steps[0], WorldMapModel.CurrentStep(zoe, Quests([], ("Sub_Zoe02", 1))));
        Assert.Equal(zoe.Blocks, WorldMapModel.Step(zoe, Quests([], ("Sub_Zoe02", 99))));
        Assert.Null(WorldMapModel.CurrentStep(zoe, Quests(["Sub_Zoe02"])));
        Assert.Null(WorldMapModel.CurrentStep(zoe, null));
    }

    [Fact]
    public void AGiver_IsFadedWhenEveryQuestIsDone_AndMarkedWhileOneIsNotTaken()
    {
        var giver = Map.Points.First(p => p.Category == "quest_giver" && p.Quests!.Count > 1);

        Assert.Empty(WorldMapModel.GiverStates(Map.Points, null));
        Assert.Equal(2, WorldMapModel.GiverStates(Map.Points, Quests([]))[giver.Id]);
        Assert.Equal(1, WorldMapModel.GiverStates(Map.Points, Quests(giver.Quests!.ToArray()))[giver.Id]);
        var running = Quests(giver.Quests!.Skip(1).ToArray(), (giver.Quests![0], 0));
        Assert.False(WorldMapModel.GiverStates(Map.Points, running).ContainsKey(giver.Id));
    }

    // ---- predators (T149) -------------------------------------------------------------------------------------

    [Fact]
    public void APredator_HasALayer_AndTheWildAreasOfItsBaseSpecies()
    {
        var data = PlannerStateServiceTests.GameDataForTests;
        var layers = Layers.Where(l => l.Predator != null).ToList();
        Assert.Equal(43, layers.Count);
        Assert.Equal(43, WorldMapModel.Enabled(["predator"], Layers).Count);
        Assert.DoesNotContain(WorldMapModel.Enabled(null, Layers), id => id.StartsWith("predator", StringComparison.Ordinal));

        var first = Map.Predators[0];
        var areas = WorldMapModel.PredatorAreas([first], data.Pals, "MainMap").Concat(WorldMapModel.PredatorAreas([first], data.Pals, "Tree")).ToList();
        var wild = data.Pals.Single(p => p.Id == first.PalId).Obtain!.Wild;
        Assert.Equal(wild.Sum(w => w.Points.Count), areas.Count);
        Assert.All(Map.Predators, p => Assert.NotEmpty(WorldMapModel.PredatorAreas([p], data.Pals, "MainMap").Concat(WorldMapModel.PredatorAreas([p], data.Pals, "Tree"))));
    }

    // ---- bases (T151) -----------------------------------------------------------------------------------------

    [Fact]
    public void TheBases_AreAddedToTheMap_NumberedWhenUnnamed_AndCanBeSwitchedOff()
    {
        var spot = Map.Points.First(p => p.Category == "fast_travel" && p.Map == "MainMap");
        var markers = WorldMapModel.BaseMarkers(
            [new BaseSite("aa-1", spot.X, spot.Y, 3500, "Home"), new BaseSite("bb-2", spot.X + 100, spot.Y)], n => "Base " + n, "en");
        var map = Map.With(WorldMapModel.BaseCategory, markers);

        Assert.Equal(["Home", "Base 2"], markers.Select(m => m.Names!.Get("en")));
        Assert.Equal("base:AA1", markers[0].Id);
        Assert.Equal(3500, markers[0].Range);
        Assert.Equal((spot.MapX, spot.MapY), (markers[0].MapX, markers[0].MapY));
        Assert.NotNull(map.TryGetPoint("base:AA1")!.RegionId);
        var categories = map.Categories.Select(c => c.Id).ToList();
        Assert.True(categories.IndexOf("base") > categories.IndexOf("fast_travel") && categories.IndexOf("base") < categories.IndexOf("merchant"));
        Assert.Same(Map, Map.With(WorldMapModel.BaseCategory, []));

        var layers = WorldMapModel.Layers(map, PlannerStateServiceTests.GameDataForTests.Relics);
        Assert.Contains("base", WorldMapModel.Enabled(["egg"], layers));
        Assert.DoesNotContain("base", WorldMapModel.Enabled(["egg", "base"], layers, hideBases: true));
    }

    [Fact]
    public void TheWorkersOfABase_ComeInSlotOrder_EachPalOnce()
    {
        OwnedPal Pal(string uid, string source, string? baseId, int slot, int minutes, string? instance = null) => new(
            uid, instance, "SheepBall", null, 1, 1, [], null, [], [], null, null,
            new PalLocation(LocationKind.Base, null, slot, baseId), PalSourceKind.Save, source, DateTimeOffset.UnixEpoch.AddMinutes(minutes), false, false);

        var workers = WorldMapModel.Workers(
            [
                Pal("a", "save", "AA-1", 3, 0, "11-22"), Pal("a-mod", "mod", "aa1", 1, 5, "1122"), Pal("b", "save", "aa-1", 0, 0),
                Pal("other base", "save", "bb-2", 0, 0), Pal("other world", "old", "aa-1", 0, 0),
            ],
            "AA1", ["save", "mod"]);

        Assert.Equal(["b", "a-mod"], workers.Select(p => p.Uid));
    }

    // ---- search -----------------------------------------------------------------------------------------------

    [Fact]
    public void Search_FindsATowerByItsSchematicOrProduct_AnEffigyType_AndAQuest()
    {
        var data = PlannerStateServiceTests.GameDataForTests;

        var towers = MapSearchEngine.Search("revolver", "en", "MainMap", Map, data).Where(h => h.Kind == MapSearchKind.Tower).ToList();
        Assert.NotEmpty(towers);
        Assert.All(towers, h => Assert.Equal("pickup_tower", Map.TryGetPoint(h.PointId!)!.Category));
        Assert.Contains(MapSearchEngine.Search("musket", "en", "MainMap", Map, data), h => h.Kind == MapSearchKind.Tower);
        Assert.DoesNotContain(MapSearchEngine.Search("dog coin", "en", "MainMap", Map, data), h => h.Kind == MapSearchKind.Tower);

        var effigy = Assert.Single(MapSearchEngine.Search("pengullet effigy", "en", "MainMap", Map, data), h => h.Kind == MapSearchKind.Effigy);
        Assert.Equal("effigy:SwimSpeed", effigy.CategoryId);

        Assert.Contains(MapSearchEngine.Search("syndicate", "en", "MainMap", Map, data), h => h.PointId == "tower:GrassBoss");
        Assert.Contains(MapSearchEngine.Search("lyleen", "en", "MainMap", Map, data), h => h.PointId == "tower:ForestBoss");
        var predator = Map.Predators[0];
        Assert.Contains(MapSearchEngine.Search(predator.Names.Get("en"), "en", "MainMap", Map, data),
            h => h.Kind == MapSearchKind.Predator && h.CategoryId == "predator:" + predator.PalId);

        var quest = Assert.Single(MapSearchEngine.Search("pal's blessing", "en", "MainMap", Map, data), h => h.Kind == MapSearchKind.Quest);
        Assert.Equal("Sub_Farmer01", quest.Id);
        Assert.NotNull(quest.PointId);
    }
}
