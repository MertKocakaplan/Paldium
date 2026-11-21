using System.Text.Json;
using Paldium.Core.Data.Schema;
using Paldium.DataBuilder;

namespace Paldium.Core.Tests;

public class DataBundleTests
{
    private static readonly Lazy<GameDataFile> GameData = new(() =>
        JsonSerializer.Deserialize<GameDataFile>(
            File.ReadAllText(Path.Combine(TestPaths.DataFolder, "gamedata.json")), SchemaJson.Indented)!);

    private static readonly Lazy<BreedingFile> Breeding = new(() =>
        JsonSerializer.Deserialize<BreedingFile>(
            File.ReadAllText(Path.Combine(TestPaths.DataFolder, "breeding.json")), SchemaJson.Compact)!);

    private static PalDto Pal(string id) => GameData.Value.Pals.Single(p => p.Id == id);

    private static string Child(string a, string b)
    {
        var order = Breeding.Value.PalOrder.ToList();
        int i = order.IndexOf(a), j = order.IndexOf(b);
        if (i > j) (i, j) = (j, i);
        var n = order.Count;
        return order[Breeding.Value.Children[i * n - i * (i - 1) / 2 + (j - i)]];
    }

    [Fact]
    public void Mounts_HaveKindsSaddleLevelsSwimSpeedsAndPartnerSkillBonuses()
    {
        var mounts = GameData.Value.Pals.Where(p => p.Mount != null).ToList();
        Assert.Equal(121, mounts.Count);
        bool Is(PalDto p, string kind) => p.Mount!.Kinds.Contains(kind);
        Assert.Equal(83, mounts.Count(p => Is(p, "Ground")));    // the community site lists 75: 6 Terraria Pals, ThunderDog_Ice and one more are counted here
        Assert.Equal(29, mounts.Count(p => Is(p, "Flying")));
        Assert.Equal(19, mounts.Count(p => Is(p, "Water")));
        Assert.All(mounts, p => Assert.Equal(5, p.Mount!.MoveByStar.Count));
        Assert.Equal(7, mounts.Count(p => p.Mount!.SaddleLevel == null));   // Whalaska and the six Terraria Pals have no saddle

        // Direhowl (Garm): the star bonus starts at 1 star (10%) and reaches 20% at 4 stars; Jetragon has none.
        var garm = Pal("Garm");
        Assert.Equal([0, 10, 12, 15, 20], garm.Mount!.MoveByStar);
        Assert.Equal((800, 1050, 70), (garm.Stats.RunSpeed, garm.Stats.RideSprintSpeed, garm.Stats.Stamina));
        Assert.Equal(9, garm.Mount.SaddleLevel);
        var jet = Pal("JetDragon");
        Assert.All(jet.Mount!.MoveByStar, v => Assert.Equal(0, v));
        Assert.Equal((1700, 3300, 110), (jet.Stats.RunSpeed, jet.Stats.RideSprintSpeed, jet.Stats.Stamina));
        Assert.Equal(70, jet.Mount.SaddleLevel);

        // Surfent (Serpent): a water mount with swim speeds and a swim bonus by star.
        var serpent = Pal("Serpent");
        Assert.Contains("Water", serpent.Mount!.Kinds);
        Assert.Equal(900, serpent.Mount.SwimSpeed);
        Assert.Contains(Pal("BlueDragon").Mount!.SwimByStar, v => v > 0);   // Azurobe: its partner skill raises the swim speed by star

        Assert.Null(Pal("SheepBall").Mount);
        Assert.Contains(mounts, p => p.Mount!.Conditional != null);
    }

    [Fact]
    public void Relics_Are13Types_WithTheRanksAndTotalsOfTheGameTable()
    {
        var relics = GameData.Value.Relics!;
        Assert.Equal(13, relics.Count);
        Assert.Equal(279, relics.Sum(r => r.Ranks.Count));
        RelicDto Relic(string type) => relics.Single(r => r.Type == type);
        int Total(string type) => Relic(type).Ranks.Sum(r => r.Required);

        Assert.Equal((92, 287), (Relic("MoveSpeed").Ranks.Count, Total("MoveSpeed")));     // Mimog
        Assert.Equal((15, 100), (Relic("CapturePower").Ranks.Count, Total("CapturePower")));   // Lifmunk (1.0: 15 ranks, 100 effigies)
        Assert.Equal((4, 4), (Relic("SphereHoming").Ranks.Count, Total("SphereHoming")));      // Lunaris
        Assert.Equal((20, 20), (Relic("SwimSpeed").Ranks.Count, Total("SwimSpeed")));
        Assert.Equal(100, Relic("SwimSpeed").Ranks[^1].Effect);
        Assert.Equal(50, Relic("MoveSpeed").Ranks[^1].Effect);

        // Every type has its effigy item, its names and a description in both languages; the Lifmunk effigy is the plain "Relic" item.
        Assert.Equal("Relic", Relic("CapturePower").ItemId);
        Assert.Equal("Mimog Effigy", Relic("MoveSpeed").ItemNames["en"]);
        Assert.Equal("Movement Speed", Relic("MoveSpeed").Names["en"]);
        Assert.All(relics, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Names["en"] + r.Names["tr"] + r.Descriptions["en"] + r.Descriptions["tr"] + r.ItemNames["en"] + r.ItemNames["tr"]));
            Assert.True(File.Exists(Path.Combine(TestPaths.DataFolder, r.Icon!)), r.Icon);
            Assert.Equal(Enumerable.Range(1, r.Ranks.Count), r.Ranks.Select(k => k.Rank));
        });
    }

    [Fact]
    public void Technologies_AreTheGamesTree_80Levels_51Ancient_NoReferenceTagsLeft()
    {
        var all = GameData.Value.Technologies!;
        Assert.Equal(588, all.Count);
        Assert.Equal(51, all.Count(t => t.Ancient));
        Assert.Equal(80, all.Select(t => t.Level).Distinct().Count());
        Assert.Equal(1, all.Min(t => t.Level));
        Assert.Equal(80, all.Max(t => t.Level));

        // Level 1, 2 and 80 as in the game's tree.
        Assert.Equal(5, all.Count(t => t.Level == 1));
        Assert.Equal(7, all.Count(t => t.Level == 2));
        Assert.Equal(4, all.Count(t => t.Level == 80));
        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, all.Where(t => t.Level == 1).Select(t => t.Cost).ToArray());
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 1, 2 }, all.Where(t => t.Level == 2).Select(t => t.Cost).OrderBy(c => c).ToArray());
        Assert.Equal("Wooden Structure Set", all.Single(t => t.Id == "Wooden_houseset").Names["en"]);
        Assert.Equal(19, all.Single(t => t.Id == "Wooden_houseset").Unlocks.Count);

        // Prerequisites: a technology, a tower boss and a research, with the game's texts.
        Assert.Equal("GrapplingGun", all.Single(t => t.Id == "GrapplingGun2").Requires!.TechnologyId);
        Assert.Equal("Mega Grappling Gun", all.Single(t => t.Id == "GrapplingGun2").Names["en"]);
        Assert.Equal("Rayne Syndicate Tower", all.First(t => t.Requires?.TowerBoss?.Type == "GrassBoss").Requires!.TowerBoss!.Names["en"]);
        Assert.Equal("Seeding5", all.Single(t => t.Id == "Silo").Requires!.Research!.Id);
        Assert.False(string.IsNullOrWhiteSpace(all.Single(t => t.Id == "Silo").Requires!.Research!.Names["en"]));
        Assert.Equal(17, all.Count(t => t.Requires?.TowerBoss != null));
        Assert.Equal(10, all.Count(t => t.Requires?.Research != null));

        // No reference tag or markup bar is left in any text; every name exists in both languages; the icons are files.
        foreach (var t in all)
        {
            var texts = t.Names.Values.Concat(t.Descriptions.Values).Concat(t.Unlocks.SelectMany(u => u.Names.Values)).ToList();
            Assert.All(texts, x => Assert.DoesNotMatch("[<>|]", x));
            Assert.False(string.IsNullOrWhiteSpace(t.Names["en"] + t.Names["tr"]), t.Id);
            Assert.All(t.Unlocks, u => Assert.False(string.IsNullOrWhiteSpace(u.Names["en"] + u.Names["tr"]), t.Id + "/" + u.Id));
        }

        var icons = all.Select(t => t.Icon).Concat(all.SelectMany(t => t.Unlocks).Select(u => u.Icon)).Where(i => i != null).Distinct().ToList();
        Assert.All(icons, i => Assert.True(File.Exists(Path.Combine(TestPaths.DataFolder, i!)), i));
        Assert.True(all.Count(t => t.Icon == null) <= 5);
    }

    [Fact]
    public void Counts_MatchAcceptanceCriteria()
    {
        Assert.Equal(2, GameData.Value.Meta.SchemaVersion);
        Assert.Equal(299, GameData.Value.Pals.Count);
        Assert.True(GameData.Value.Passives.Count >= 115);
        Assert.Equal(115, GameData.Value.Passives.Count(p => p.IsStandard));
        Assert.Equal(320, GameData.Value.Actives.Count);
        Assert.Equal(299 * 300 / 2, Breeding.Value.Children.Count);
        Assert.Equal(299, Breeding.Value.PalOrder.Count);
        Assert.Equal(2, Breeding.Value.GenderSpecific.Count);
    }

    [Fact]
    public void Meta_RecordsGameVersionAndSource()
    {
        var meta = GameData.Value.Meta;
        Assert.Equal("1.0.5", meta.GameVersion);
        Assert.Equal("25246127", meta.GameBuild);
        var source = Assert.Single(meta.Sources);
        Assert.Equal("palcalc", source.Name);
        Assert.Equal("v28", source.DbVersion);
        Assert.Equal(40, source.Commit.Length);
    }

    [Fact]
    public void Sekhmet_HasExpectedValues()
    {
        var p = Pal("Sekhmet");
        Assert.Equal(870, p.BreedingPower);
        Assert.Equal(0.3, p.MaleProbability, 9);
        Assert.Equal("140", p.DexLabel);
        Assert.Equal(141, p.BreedingFlags.ParentPairCount);
        Assert.Equal(6, p.Work["Handiwork"]);
    }

    [Fact]
    public void Mozzarina_HasExpectedMaleProbability()
    {
        Assert.Equal(0.2, Pal("CowPal").MaleProbability, 9);
    }

    [Fact]
    public void Variants_GetBSuffixInDexLabel()
    {
        Assert.Equal("94B", Pal("LazyDragon_Electric").DexLabel);
        Assert.True(Pal("LazyDragon_Electric").IsVariant);
        Assert.False(Pal("LazyDragon").IsVariant);
    }

    [Fact]
    public void SameSpeciesOnly_IsTrueForJetragon()
    {
        Assert.True(Pal("JetDragon").BreedingFlags.SameSpeciesOnly);
        Assert.False(Pal("Sekhmet").BreedingFlags.SameSpeciesOnly);
    }

    [Fact]
    public void BreedingMatrix_Examples()
    {
        Assert.Equal("Sekhmet", Child("LazyDragon", "WhiteMoth_Neutral"));
        Assert.Equal("Sekhmet", Child("WhiteMoth_Neutral", "LazyDragon"));
        Assert.Equal("JetDragon", Child("JetDragon", "JetDragon"));
    }

    [Fact]
    public void GenderSpecificRows_CoverCatMageAndFoxMage()
    {
        var rows = Breeding.Value.GenderSpecific;
        Assert.Contains(rows, r => r is { A: "CatMage", AGender: "Female", B: "FoxMage", BGender: "Male", Child: "CatMage_Fire" });
        Assert.Contains(rows, r => r is { A: "CatMage", AGender: "Male", B: "FoxMage", BGender: "Female", Child: "FoxMage_Dark" });
    }

    [Fact]
    public void EveryPal_HasNameAndExistingIcon()
    {
        foreach (var p in GameData.Value.Pals)
        {
            Assert.True(p.Names.Values.Any(v => !string.IsNullOrWhiteSpace(v) && v != "-"), p.Id + " has no name");
            Assert.True(File.Exists(Path.Combine(TestPaths.DataFolder, p.Icon)), p.Id + " icon missing: " + p.Icon);
        }
    }

    [Fact]
    public void StandardPassives_AllTagged_AndFewOther()
    {
        var standard = GameData.Value.Passives.Where(p => p.IsStandard).ToList();
        Assert.All(standard, p => Assert.NotEmpty(p.Tags));
        Assert.True(standard.Count(p => p.Tags.Contains("Other")) <= 10);
        Assert.All(GameData.Value.Passives.Where(p => p.Rank < 0), p => Assert.Contains("Negative", p.Tags));
    }

    [Fact]
    public void WorkSpeedTag_IsOnlyOnPassivesThatImproveWorkSpeed()
    {
        var ids = GameData.Value.Passives.Where(p => p.Tags.Contains("WorkSpeed")).Select(p => p.Id).Order().ToList();
        string[] expected =
        [
            "CraftSpeed_up1",       // Serious
            "CraftSpeed_up2",       // Artisan
            "CraftSpeed_up3",       // Remarkable Craftsmanship
            "PAL_CorporateSlave",   // Work Slave
            "PAL_conceited",        // Conceited
            "Rare",                 // Lucky
            "WorldTree_CraftSpeed", // Demon's Hand
        ];
        Assert.Equal(expected.Order().ToList(), ids);
    }

    [Fact]
    public void PenaltyTags_ListWhatAPassiveWorsens()
    {
        var musclehead = GameData.Value.Passives.Single(p => p.Id == "Noukin");
        Assert.DoesNotContain("WorkSpeed", musclehead.Tags);
        Assert.Contains("Attack", musclehead.Tags);
        Assert.Equal(["WorkSpeed"], musclehead.PenaltyTags!);

        var hooligan = GameData.Value.Passives.Single(p => p.Id == "PAL_rude");
        Assert.DoesNotContain("WorkSpeed", hooligan.Tags);
        Assert.Contains("WorkSpeed", hooligan.PenaltyTags!);

        var slave = GameData.Value.Passives.Single(p => p.Id == "PAL_CorporateSlave");
        Assert.Equal(["Attack"], slave.PenaltyTags!);

        var demonsHand = GameData.Value.Passives.Single(p => p.Id == "WorldTree_CraftSpeed");
        Assert.Equal(["WorldTree", "WorkSpeed"], demonsHand.Tags);
        Assert.Equal(["Sanity"], demonsHand.PenaltyTags!);
    }

    [Fact]
    public void NegativePassives_HaveOnlyNegativeTag_AndPenaltyCategories()
    {
        var negative = GameData.Value.Passives.Where(p => p.Rank < 0).ToList();
        Assert.NotEmpty(negative);
        Assert.All(negative, p => Assert.Equal(["Negative"], p.Tags));
        Assert.All(negative, p => Assert.NotEmpty(p.PenaltyTags ?? []));
        Assert.Contains("WorkSpeed", negative.Single(p => p.Id == "CraftSpeed_down2").PenaltyTags!);
    }

    [Fact]
    public void Passives_IncludeEveryGuaranteedPassive()
    {
        var ids = GameData.Value.Passives.Select(p => p.Id).ToHashSet();
        Assert.All(GameData.Value.Pals.SelectMany(p => p.GuaranteedPassives), id => Assert.Contains(id, ids));
    }

    [Fact]
    public void Mechanics_MatchArchitectureConstants()
    {
        var m = GameData.Value.Mechanics;
        Assert.Equal(4, m.MaxPassives);
        Assert.Equal([4, 3, 2, 1], m.PassiveInheritWeights.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value));
        Assert.Equal([4, 3, 2, 1], m.PassiveRandomAddWeights.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value));
        Assert.Equal(5, m.Cakes.Count);
        Assert.Equal(4, m.Cakes.Single(c => c.Id == "Cake05").PassiveInheritCountOverride);
    }

    [Fact]
    public void EveryPal_HasElementsAndEggElement()
    {
        Assert.All(GameData.Value.Pals, p =>
        {
            Assert.NotEmpty(p.Elements);
            Assert.Equal(p.Elements[0], p.Egg.Element);
        });
        var elementIds = GameData.Value.Elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(GameData.Value.Pals, p => Assert.All(p.Elements, e => Assert.Contains(e, elementIds)));
        Assert.Equal(["Earth"], Pal("Anubis").Elements);
    }

    [Fact]
    public void PartnerSkills_HaveEnglishAndTurkishNames()
    {
        Assert.All(GameData.Value.Pals, p =>
        {
            Assert.NotNull(p.PartnerSkill);
            Assert.False(string.IsNullOrWhiteSpace(p.PartnerSkill!.Names["en"]), p.Id);
            Assert.False(string.IsNullOrWhiteSpace(p.PartnerSkill.Names["tr"]), p.Id);
            Assert.False(string.IsNullOrWhiteSpace(p.PartnerSkill.Descriptions["en"]), p.Id);
            Assert.DoesNotContain("<", p.PartnerSkill.Descriptions["en"]);
        });
    }

    [Fact]
    public void Mozzarina_HasDropsWithLocalizedNames()
    {
        var drops = Pal("CowPal").Drops;
        Assert.NotNull(drops);
        Assert.NotEmpty(drops!);
        Assert.All(drops, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Names["en"]), d.ItemId);
            Assert.False(string.IsNullOrWhiteSpace(d.Names["tr"]), d.ItemId);
            Assert.InRange(d.Rate, 0.0, 1.0);
        });
    }

    [Fact]
    public void Passives_CarryEffects()
    {
        var alien = GameData.Value.Passives.Single(p => p.Id == "Alien");
        Assert.NotNull(alien.Effects);
        Assert.Contains(alien.Effects!, e => e.Type == "ShotAttack" && e.Value == 10 && e.Target == "ToSelf");
    }

    [Fact]
    public void Anubis_HasAlphaBossNearKnownPositionAndWildSpawns()
    {
        var obtain = Pal("Anubis").Obtain!;
        Assert.Contains("Wild", obtain.Methods);
        Assert.Contains("AlphaBoss", obtain.Methods);
        Assert.NotEmpty(obtain.Wild);
        var alpha = Assert.Single(obtain.Bosses, b => b.Kind == "Alpha" && b.Level == 55);
        Assert.Equal("MainMap", alpha.Point.Map);
        Assert.InRange(alpha.Point.MapX!.Value, -137, -131);
        Assert.InRange(alpha.Point.MapY!.Value, -97, -91);
    }

    [Fact]
    public void Jetragon_CanBeObtainedAsAlpha()
    {
        Assert.Contains("AlphaBoss", Pal("JetDragon").Obtain!.Methods);
    }

    [Fact]
    public void Obtain_HasNightSpawnsAndLocalizedDungeons()
    {
        var all = GameData.Value.Pals.Select(p => p.Obtain!).ToList();
        Assert.Contains(all, o => o.Wild.Any(w => w.Night));
        Assert.Contains(all, o => o.Dungeons.Any(d => !string.IsNullOrEmpty(d.Names.GetValueOrDefault("tr"))));
        // Dungeon placements carry no coordinates and debug areas are skipped.
        Assert.DoesNotContain(all, o => o.Dungeons.Any(d => d.Area.StartsWith("TestDebug", StringComparison.Ordinal)));
        Assert.DoesNotContain(all, o => o.Merchants.Any(m => m.Shop.StartsWith("Test_", StringComparison.Ordinal)));
    }

    [Fact]
    public void Obtain_MethodsAreNeverEmpty_AndMapPointsAreConsistent()
    {
        Assert.All(GameData.Value.Pals, p => Assert.NotEmpty(p.Obtain!.Methods));
        var points = GameData.Value.Pals.SelectMany(p => p.Obtain!.Wild.SelectMany(w => w.Points)).ToList();
        Assert.NotEmpty(points);
        Assert.All(points, pt =>
        {
            Assert.NotNull(pt.Map);
            Assert.Equal(pt.Map == "MainMap", pt.MapX != null && pt.MapY != null);
        });
    }

    [Fact]
    public void Terms_HaveTurkishGameNames()
    {
        var terms = GameData.Value.Terms!;
        Assert.False(string.IsNullOrWhiteSpace(terms["BreedingFarm"]["tr"]));
        foreach (var id in new[] { "Cake01", "Cake02", "Cake03", "Cake04", "Cake05", "Incubator", "Palbox", "ViewingCage",
                     "DimensionalPalStorage", "GlobalPalbox", "Alpha", "PartnerSkill", "WorkSuitability", "Condensation" })
        {
            Assert.False(string.IsNullOrWhiteSpace(terms[id]["en"]), id);
            Assert.False(string.IsNullOrWhiteSpace(terms[id]["tr"]), id);
        }

        Assert.Equal("Ground", terms["Element.Earth"]["en"]);
        Assert.Equal("Toprak", terms["Element.Earth"]["tr"]);
        Assert.All(terms.Values, names => Assert.DoesNotContain(names.Values, v => v is "-" or "???"));
    }

    // The places the obtain tables name by a key (cage field, merchant shop, fishing pond group) get the game's own area name (T52). The keys that are
    // not matched stay on a provisional label of the app (resx Obtain.<kind>.<key>); this list is the REPORT's list of unmatched keys.
    private static readonly string[] UnmatchedAreaKeys =
    [
        "Field.Viking1", "Shop.Dark_01", "Shop.Dark_02", "Shop.Dark_03", "Shop.Dark_04", "Shop.Test_00", "Shop.Test_01", "Fishing.FishingSpot",
    ];

    [Fact]
    public void AreaTerms_NameTheCageFieldsShopsAndFishingPonds_InBothLanguages()
    {
        var terms = GameData.Value.Terms!;
        var areaTerms = terms.Keys.Where(k => k.StartsWith("Area.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(areaTerms);
        foreach (var id in areaTerms)
        {
            Assert.False(string.IsNullOrWhiteSpace(terms[id]["en"]), id);
            Assert.False(string.IsNullOrWhiteSpace(terms[id]["tr"]), id);
        }

        Assert.Equal("Desiccated Dunes", terms["Area.Field.Desert1"]["en"]);
        Assert.Equal(terms["Area.Field.Desert1"]["tr"], terms["Area.Field.SeaBase_Desert_1"]["tr"]); // the sea cage is named like its region (+ "sea")
        Assert.True(terms.ContainsKey("Area.Field.SeaBase_Yamijima_1"));
        Assert.True(terms.ContainsKey("BlackMarketeer"));
        Assert.Equal("Sunreach", terms["Area.Fishing.SkyIsland_Treasure_FishPond"]["en"]);

        // Every key of the obtain data either has an area term or is on the list of keys that could not be matched (nothing is raw by accident).
        var keys = GameData.Value.Pals.Where(p => p.Obtain != null).SelectMany(p => p.Obtain!.Cages.Select(c => "Field." + c.Field)
            .Concat(p.Obtain.Merchants.Select(m => "Shop." + m.Shop)).Concat(p.Obtain.Fishing.Select(f => "Fishing." + f.Group))).Distinct().ToList();
        Assert.NotEmpty(keys);
        foreach (var key in keys)
            Assert.True(terms.ContainsKey("Area." + key) || UnmatchedAreaKeys.Contains(key), key);
    }

    [Fact]
    public void WorkTypes_HaveEnglishAndTurkishNames()
    {
        Assert.Equal(12, GameData.Value.WorkTypes.Count);
        Assert.All(GameData.Value.WorkTypes, w =>
        {
            Assert.False(string.IsNullOrWhiteSpace(w.Names["en"]), w.Id);
            Assert.False(string.IsNullOrWhiteSpace(w.Names["tr"]), w.Id);
        });
        Assert.DoesNotContain(GameData.Value.WorkTypes, w => w.Id == "OilExtraction");
    }

    [Fact]
    public void WildRegions_AreNamedWhereTheMapKnowsThem()
    {
        var wild = GameData.Value.Pals.SelectMany(p => p.Obtain!.Wild).ToList();
        var named = wild.Where(w => w.RegionNames != null).ToList();
        Assert.NotEmpty(named);
        Assert.Contains(named, w => w.Region == "worldtree");
        Assert.All(named, w =>
        {
            Assert.False(string.IsNullOrWhiteSpace(w.RegionNames!["en"]));
            Assert.False(string.IsNullOrWhiteSpace(w.RegionNames["tr"]));
        });
        // The unmapped ones keep their raw region key.
        Assert.All(wild.Where(w => w.RegionNames == null), w => Assert.False(string.IsNullOrEmpty(w.Region)));
    }

    [Fact]
    public void Builder_IsDeterministic()
    {
        var palcalc = Path.Combine(TestPaths.RepoRoot, "external", "palcalc");
        var tags = Path.Combine(TestPaths.RepoRoot, "tools", "Paldium.DataBuilder", "passive-tags.json");
        var tmp = Path.Combine(Path.GetTempPath(), "pb-det-" + Guid.NewGuid().ToString("N"));
        try
        {
            var outputs = new List<(string Game, string Breed)>();
            for (var run = 0; run < 2; run++)
            {
                var dir = Path.Combine(tmp, run.ToString());
                var src = new PalCalcSource(palcalc);
                var (g, b) = src.Build("2026-11-12T00:00:00Z", "test", tags, dir);
                BundleWriter.Write(dir, g, b);
                outputs.Add((File.ReadAllText(Path.Combine(dir, "gamedata.json")),
                    File.ReadAllText(Path.Combine(dir, "breeding.json"))));
            }

            Assert.Equal(outputs[0].Game, outputs[1].Game);
            Assert.Equal(outputs[0].Breed, outputs[1].Breed);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Ranch_KnownPals_DropTheRightItems()
    {
        // T108: Lamball drops Wool and Chikipi an Egg in the Ranch; the amount grows with the rank (1 to 10).
        var data = Paldium.Core.Data.GameDataLoader.Load(TestPaths.DataFolder);
        var lamball = data.Pal("SheepBall").Ranch;
        Assert.Equal(10, lamball.Count);
        var first = Assert.Single(lamball[0].Drops);
        Assert.Equal("Wool", first.ItemId);
        Assert.Equal("Yün", first.Names.Get("tr"));
        Assert.Equal(1.0, first.Chance, 6);
        Assert.True(lamball[9].Drops[0].Max > lamball[0].Drops[0].Max);
        Assert.Equal("Egg", Assert.Single(data.Pal("ChickenPal").Ranch[0].Drops).ItemId);
        Assert.Empty(data.Pal("Sekhmet").Ranch);
        Assert.Equal(28, data.Pals.Count(p => p.Ranch.Count > 0));   // 32 have spawn item data; the four without Ranch work (Farming) are left out (T116)
    }

    [Fact]
    public void Ranch_MultiItemPals_HaveChancesThatSumToOne_AndRankChangesThem()
    {
        var data = Paldium.Core.Data.GameDataLoader.Load(TestPaths.DataFolder);
        var vixy = data.Pal("CuteFox").Ranch;
        foreach (var rank in vixy) Assert.Equal(1.0, rank.Drops.Sum(d => d.Chance), 4);
        Assert.True(vixy[0].Drops.Count < vixy[9].Drops.Count);   // better spheres appear at higher ranks
        Assert.DoesNotContain(vixy[0].Drops, d => d.ItemId == "PalSphere_Tera");
        Assert.Contains(vixy[9].Drops, d => d.ItemId == "PalSphere_Tera");
        Assert.True(data.Pals.Count(p => p.Ranch.Count > 0 && p.Ranch[0].Drops.Count > 1) >= 3);
    }

    [Fact]
    public void Ranch_EveryItemHasAnExistingIcon()
    {
        var data = Paldium.Core.Data.GameDataLoader.Load(TestPaths.DataFolder);
        foreach (var drop in data.Pals.SelectMany(p => p.Ranch).SelectMany(r => r.Drops).DistinctBy(d => d.ItemId))
        {
            Assert.NotNull(drop.IconPath);
            Assert.True(File.Exists(Path.Combine(TestPaths.DataFolder, drop.IconPath!)), drop.ItemId + " icon missing");
            Assert.False(string.IsNullOrWhiteSpace(drop.Names.Get("tr")));
        }
    }
}
