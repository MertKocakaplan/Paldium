using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// Game terms the UI shows (cakes, buildings, egg names, element and work suitability names, ...) as
/// <c>termId -&gt; {en, tr}</c> taken from the L10N tables. Output: <c>terms.json</c>.
/// </summary>
public static class LocalizationExporter
{
    private sealed record Def(string Id, string Table, string Key);

    // Element ids of the data bundle -> game element names (the same here), plus the egg families.
    private static readonly string[] Elements = ["Normal", "Fire", "Water", "Leaf", "Electricity", "Ice", "Earth", "Dark", "Dragon"];
    private static readonly string[] EggFamilies = [.. Elements, "MutationPal", "WorldTree"];

    // Work type id of the data bundle -> COMMON_WORK_SUITABILITY_<name> suffix (same mapping as DataBuilder WorkMap).
    private static readonly (string Bundle, string Game)[] WorkTypes =
    [
        ("Kindling", "EmitFlame"), ("Watering", "Watering"), ("Planting", "Seeding"),
        ("GenerateElectricity", "GenerateElectricity"), ("Handiwork", "Handcraft"), ("Gathering", "Collection"),
        ("Lumbering", "Deforest"), ("Mining", "Mining"), ("MedicineProduction", "ProductMedicine"),
        ("Cooling", "Cool"), ("Transporting", "Transport"), ("Farming", "MonsterFarm"), ("OilExtraction", "OilExtraction"),
    ];

    // Area names for the places the obtain tables only know by a key (cage field, merchant shop, fishing pond group): the key is matched to the game's
    // region name (the world map text table) by the name inside the key (Forest01 -> REGION_Forest_1, Snow -> Frost, ...). Keys that have no region
    // here (the sea areas, the Viking field, the test and dark shops, the generic fishing spot) are not guessed: the app keeps its provisional
    // label for them and the REPORT lists them (T52).
    private static readonly (string Id, string Key)[] AreaNames =
    [
        ("Area.Field.Grass", "REGION_Grass_1"), ("Area.Field.Grass2", "REGION_Grass_2"), ("Area.Field.Forest1", "REGION_Forest_1"),
        ("Area.Field.Desert1", "REGION_Desert_1"), ("Area.Field.Snow1", "REGION_Frost_1"), ("Area.Field.Volcano1", "REGION_Volcano_1"),
        ("Area.Field.Sakurajima1", "REGION_Wide_Sakurajima01"), ("Area.Field.Skyisland1", "REGION_Wide_Skyisland01"),
        ("Area.Field.SeaBase_Desert_1", "REGION_Desert_1"), ("Area.Field.SeaBase_Grass_1", "REGION_Grass_1"),
        ("Area.Field.SeaBase_Sakurajima_1", "REGION_Wide_Sakurajima01"), ("Area.Field.SeaBase_Snow_1", "REGION_Frost_1"),
        ("Area.Field.SeaBase_Volcano_1", "REGION_Volcano_1"), ("Area.Field.SeaBase_Yamijima_1", "REGION_Wide_Darkisland01"),
        ("Area.Shop.Desert_00", "REGION_Desert_1"), ("Area.Shop.Volcano_00", "REGION_Volcano_1"),
        ("Area.Fishing.Forest01_FishPond", "REGION_Forest_1"), ("Area.Fishing.Grass01_FishPond", "REGION_Grass_1"),
        ("Area.Fishing.Desert01_FishPond", "REGION_Desert_1"), ("Area.Fishing.Snow01_FishPond", "REGION_Frost_1"),
        ("Area.Fishing.Volcano01_FishPond", "REGION_Volcano_1"), ("Area.Fishing.Sakurajima_Treasure_FishPond", "REGION_Wide_Sakurajima01"),
        ("Area.Fishing.SkyIsland_Treasure_FishPond", "REGION_Wide_Skyisland01"), ("Area.Fishing.DarkIsland_Treasure_FishPond", "REGION_Wide_Darkisland01"),
        ("Area.Fishing.WorldTree_Treasure_FishPond", "WORLDMAP_NAME_Tree"),
    ];

    private static IEnumerable<Def> Definitions()
    {
        // Cake01 is the plain "Cake" item (ITEM_NAME_Cake); Cake02..05 have their own keys.
        foreach (var (id, key) in AreaNames) yield return new Def(id, "worldMap", key);

        yield return new Def("Cake01", "itemName", "ITEM_NAME_Cake");
        for (var i = 2; i <= 5; i++) yield return new Def($"Cake0{i}", "itemName", $"ITEM_NAME_Cake0{i}");

        yield return new Def("BreedingFarm", "mapObjectName", "MAPOBJECT_NAME_BreedFarm");
        yield return new Def("Incubator", "mapObjectName", "MAPOBJECT_NAME_HatchingPalEgg");
        yield return new Def("IncubatorElectric", "mapObjectName", "MAPOBJECT_NAME_ElectricHatchingPalEgg");
        yield return new Def("IncubatorLarge", "mapObjectName", "MAPOBJECT_NAME_MultiHatchingPalEgg");
        yield return new Def("IncubatorLargeElectric", "mapObjectName", "MAPOBJECT_NAME_MultiElectricHatchingPalEgg");

        yield return new Def("Egg", "itemName", "ITEM_NAME_PalEgg");
        foreach (var family in EggFamilies)
        {
            yield return new Def($"Egg.{family}.Normal", "itemName", $"ITEM_NAME_PalEgg_{family}_01");
            yield return new Def($"Egg.{family}.Large", "itemName", $"ITEM_NAME_PalEgg_{family}_03");
            yield return new Def($"Egg.{family}.Huge", "itemName", $"ITEM_NAME_PalEgg_{family}_05");
        }

        yield return new Def("Palbox", "mapObjectName", "MAPOBJECT_NAME_PalBoxV2");
        yield return new Def("ViewingCage", "mapObjectName", "MAPOBJECT_NAME_DisplayCharacter");
        yield return new Def("DimensionalPalStorage", "mapObjectName", "MAPOBJECT_NAME_DimensionPalStorage");
        yield return new Def("GlobalPalbox", "mapObjectName", "MAPOBJECT_NAME_GlobalPalStorage");
        yield return new Def("CondensationMachine", "mapObjectName", "MAPOBJECT_NAME_CharacterRankUp");

        // "Alpha Pal" is the game's label for boss-flagged Pals (no standalone "Alpha" text exists).
        yield return new Def("Alpha", "uiCommon", "PAL_BOX_SORTTYPE_BOSS_FLAG");
        yield return new Def("FieldBoss", "uiCommon", "MAP_FILTER_BOSS");
        yield return new Def("Tower", "uiCommon", "MAP_FILTER_BOSSTOWER");
        yield return new Def("PartnerSkill", "uiCommon", "COMMON_COOP_ACTION");
        yield return new Def("PassiveSkills", "uiCommon", "COMMON_CHARACTERISTIC");
        yield return new Def("ActiveSkills", "uiCommon", "COMMON_ACTIVE_SKILL");
        yield return new Def("WorkSuitability", "uiCommon", "COMMON_WORK_SUITABILITY");
        yield return new Def("Condensation", "uiCommon", "RANKUP_TITLE");
        yield return new Def("Raid", "uiCommon", "WARNING_INVADER_START_TITLE");
        yield return new Def("Fishing", "uiCommon", "COMMON_WORK_TYPE_FishPond");
        yield return new Def("Level", "uiCommon", "PAL_BOX_SORTTYPE_LEVEL_DESC");
        yield return new Def("Gender", "uiCommon", "PALBOX_FILTERING_GENDER");
        yield return new Def("WanderingMerchant", "humanName", "NAME_SELESPERSON");
        yield return new Def("BlackMarketeer", "humanName", "NAME_DarkTrader");
        yield return new Def("Map.MainMap", "worldMap", "WORLDMAP_NAME_MainMap");
        yield return new Def("Map.Tree", "worldMap", "WORLDMAP_NAME_Tree");

        foreach (var element in Elements)
            yield return new Def($"Element.{element}", "uiCommon", $"COMMON_ELEMENT_NAME_{element}");
        foreach (var (bundle, game) in WorkTypes)
            yield return new Def($"Work.{bundle}", "uiCommon", $"COMMON_WORK_SUITABILITY_{game}");
    }

    // The game ships placeholders for unfinished texts.
    private static bool IsPlaceholder(string text) =>
        text.Length == 0 || text is "-" or "???" or "en_text" || text.EndsWith(" Text", StringComparison.Ordinal);

    public static JObject Export(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> texts, List<string> warnings)
    {
        var terms = new JObject();
        var missing = new JArray();
        foreach (var def in Definitions())
        {
            var entry = new JObject();
            foreach (var language in TextExporter.Languages)
            {
                if (!texts[language].TryGetValue(def.Table, out var table) || !table.TryGetValue(def.Key, out var raw)) continue;
                var text = TextExporter.Plain(raw, texts[language]);
                if (!IsPlaceholder(text)) entry[language] = text;
            }

            if (entry.Count == 0) { missing.Add(def.Id); continue; }
            entry["source"] = $"{def.Table}:{def.Key}";
            terms[def.Id] = entry;
            foreach (var language in TextExporter.Languages)
                if (entry[language] == null) missing.Add($"{def.Id} ({language})");
        }

        warnings.Add($"terms: {terms.Count} found, {missing.Count} missing or incomplete.");
        return new JObject { ["terms"] = terms, ["missing"] = missing };
    }
}
