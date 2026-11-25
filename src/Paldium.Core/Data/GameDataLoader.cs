using System.Text.Json;
using Paldium.Core.Data.Schema;
using Paldium.Core.Domain;

namespace Paldium.Core.Data;

/// <summary>Loads data/gamedata.json into the domain model.</summary>
public static class GameDataLoader
{
    public const string FileName = "gamedata.json";

    /// <summary>
    /// Reads <c>gamedata.json</c> from the folder. Throws <see cref="DirectoryNotFoundException"/> /
    /// <see cref="FileNotFoundException"/> when the bundle is missing and <see cref="InvalidDataException"/>
    /// when the schema version is not supported or the file is malformed.
    /// </summary>
    public static GameData Load(string dataFolder)
    {
        if (!Directory.Exists(dataFolder))
            throw new DirectoryNotFoundException($"Game data folder not found: {dataFolder}");

        var path = Path.Combine(dataFolder, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Game data file not found: {path}", path);

        var bytes = File.ReadAllBytes(path);
        CheckSchemaVersion(bytes, path);

        GameDataFile file;
        try
        {
            file = JsonSerializer.Deserialize<GameDataFile>(bytes, SchemaJson.Compact)
                   ?? throw new InvalidDataException($"Game data file is empty: {path}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Game data file is malformed: {path} ({ex.Message})", ex);
        }

        return Map(file);
    }

    private static void CheckSchemaVersion(byte[] bytes, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var version = doc.RootElement.TryGetProperty("meta", out var meta) &&
                          meta.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32()
                : -1;
            if (version < SchemaJson.MinSupportedSchemaVersion || version > SchemaJson.CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"Unsupported game data schema version {version} in {path}; expected {SchemaJson.MinSupportedSchemaVersion}..{SchemaJson.CurrentSchemaVersion}. " +
                    "Rebuild the data bundle with the matching DataBuilder.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Game data file is not valid JSON: {path} ({ex.Message})", ex);
        }
    }

    private static GameData Map(GameDataFile f)
    {
        var meta = new GameDataMeta(
            f.Meta.SchemaVersion, f.Meta.GameVersion, f.Meta.GameBuild, f.Meta.GeneratedAtUtc,
            f.Meta.Sources.Select(s => new GameDataSource(s.Name, s.Commit, s.DbVersion)).ToList());

        var m = f.Mechanics;
        var mechanics = new MechanicsConfig(
            m.MaxPassives,
            ToIntKeys(m.PassiveInheritWeights),
            ToIntKeys(m.PassiveRandomAddWeights),
            m.TalentInheritWeights.ToList(),
            m.AlphaRate,
            new MutationConfig(m.Mutation.Rate, m.Mutation.RankCoefficient, m.Mutation.RankDiffPenalty,
                m.Mutation.RandomCoefficient, m.Mutation.MinTalent),
            m.WildPassiveCountWeights.ToList(),
            new Dictionary<string, int>(m.EggSizeMinRarity, StringComparer.OrdinalIgnoreCase),
            m.Cakes.Select(c => new CakeConfig(c.Id, c.TalentBonusMin, c.TalentBonusMax, c.MutationRateBonusPercent,
                c.CombiRankBonus, c.BreedCount, c.InheritAllActiveSkills, c.PassiveInheritCountOverride)).ToList());

        var pals = f.Pals.Select(p => new PalSpecies(
            p.Id, p.DexNo, p.IsVariant, p.DexLabel, p.TableOrder,
            new LocalizedText(p.Names, p.Id), p.Elements.ToList(), p.Rarity, p.Size, p.Nocturnal,
            p.BreedingPower, p.BreedingPriority, p.MaleProbability,
            new PalStats(p.Stats.Hp, p.Stats.Attack, p.Stats.Defense, p.Stats.WalkSpeed, p.Stats.RunSpeed,
                p.Stats.RideSprintSpeed, p.Stats.TransportSpeed, p.Stats.Stamina, p.Stats.MaxFullStomach,
                p.Stats.FoodAmount, p.Stats.CraftSpeed),
            new Dictionary<string, int>(p.Work, StringComparer.OrdinalIgnoreCase),
            p.WildLevel == null ? null : new LevelRange(p.WildLevel.Min, p.WildLevel.Max),
            p.AttackLearnset.Select(a => new LearnsetEntry(a.SkillId, a.Level)).ToList(),
            p.GuaranteedPassives.ToList(),
            new EggInfo(p.Egg.Element, p.Egg.Size),
            p.Icon,
            new BreedingFlags(p.BreedingFlags.SameSpeciesOnly, p.BreedingFlags.ParentPairCount, p.BreedingFlags.UniqueRecipe, p.BreedingFlags.Unbreedable),
            p.PartnerSkill is { } ps
                ? new PartnerSkillInfo(new LocalizedText(ps.Names, p.Id), new LocalizedText(ps.Descriptions, string.Empty),
                    ps.PassivesByRank.Select(r => (IReadOnlyList<string>)r.ToList()).ToList())
                {
                    PassiveIdsByRank = (ps.PassiveIdsByRank ?? []).Select(r => (IReadOnlyList<string>)r.ToList()).ToList(),
                    NumbersByRank = (ps.NumbersByRank ?? []).ToDictionary(
                        kv => kv.Key, kv => (IReadOnlyList<double?>)kv.Value.ToList(), StringComparer.Ordinal),
                    TextsByRank = (ps.TextsByRank ?? []).ToDictionary(
                        kv => kv.Key,
                        kv => (IReadOnlyDictionary<string, IReadOnlyList<string?>>)kv.Value.ToDictionary(
                            l => l.Key, l => (IReadOnlyList<string?>)l.Value.ToList(), StringComparer.OrdinalIgnoreCase),
                        StringComparer.Ordinal),
                }
                : null,
            p.Obtain == null ? null : MapObtain(p.Obtain, p.Id))
        {
            Price = p.Price,
            Group = p.Group,
            IsUncapturable = p.IsUncapturable,
            Mount = p.Mount == null ? null : new MountInfo(
                p.Mount.Kinds.ToList(), p.Mount.SaddleLevel, p.Mount.SwimSpeed, p.Mount.SwimDashSpeed, p.Mount.MoveByStar.ToList(), p.Mount.SwimByStar.ToList(),
                p.Mount.Conditional == null ? null : new MountConditional(p.Mount.Conditional.MoveByStar.ToList(), p.Mount.Conditional.SwimByStar.ToList())),
            Drops = (p.Drops ?? []).Select(d => new DropInfo(
                d.ItemId, new LocalizedText(d.Names, d.ItemId), d.Rate, d.Min, d.Max, d.MinLevel)).ToList(),
            Ranch = (p.Ranch ?? []).Select(r => new RanchRank(r.Rank, r.Drops.Select(d => new RanchDrop(
                d.ItemId, new LocalizedText(d.Names, d.ItemId), d.Icon, d.Weight, d.Chance, d.Min, d.Max)).ToList())).ToList(),
        }).ToList();

        var passives = f.Passives.Select(p => new PassiveSkill(
            p.Id, new LocalizedText(p.Names, p.Id), new LocalizedText(p.Descriptions, string.Empty), p.Rank,
            p.IsStandard, p.CanAppearRandomly, p.RandomWeight,
            new SurgeryInfo(p.Surgery.Supported, p.Surgery.Cost, p.Surgery.RequiredItem),
            p.Tags.ToList(), (p.PenaltyTags ?? []).ToList(), p.MutationExclusive)
        {
            Effects = (p.Effects ?? []).Select(e => new PassiveEffect(e.Type, e.Value, e.Target)).ToList(),
        }).ToList();

        var actives = f.Actives.Select(a => new ActiveSkill(
            a.Id, new LocalizedText(a.Names, a.Id), a.Element, a.Power, a.CooldownSeconds,
            a.CanInherit, a.HasSkillFruit)).ToList();

        var elements = f.Elements.Select(e => new ElementInfo(e.Id, new LocalizedText(e.Names, e.Id), e.Icon)).ToList();
        var workTypes = f.WorkTypes.Select(w => new WorkTypeInfo(w.Id, new LocalizedText(w.Names, w.Id), w.Icon)).ToList();

        var terms = (f.Terms ?? []).ToDictionary(
            t => t.Key, t => new LocalizedText(t.Value, t.Key), StringComparer.Ordinal);
        var relics = (f.Relics ?? []).Select(r => new RelicInfo(
            r.Type, r.Index, r.ItemId, new LocalizedText(r.ItemNames, r.ItemId), new LocalizedText(r.Names, r.Type), new LocalizedText(r.Descriptions, string.Empty), r.Icon,
            r.Ranks.Select(k => new RelicRank(k.Rank, k.Required, k.Effect)).ToList())).ToList();
        var technologies = (f.Technologies ?? []).Select(x => new TechnologyInfo(
            x.Id, x.Index, x.Level, x.Cost, x.Ancient, new LocalizedText(x.Names, x.Id), new LocalizedText(x.Descriptions, string.Empty), x.Icon,
            x.Requires == null ? null : new TechnologyRequirement(
                x.Requires.TechnologyId,
                x.Requires.TowerBoss?.Type, x.Requires.TowerBoss == null ? null : new LocalizedText(x.Requires.TowerBoss.Names, x.Requires.TowerBoss.Type),
                x.Requires.Research?.Id, x.Requires.Research == null ? null : new LocalizedText(x.Requires.Research.Names, x.Requires.Research.Id)),
            x.Unlocks.Select(u => new TechnologyUnlock(u.Kind == "build", u.Id, new LocalizedText(u.Names, u.Id), u.Icon)).ToList())).ToList();
        var shops = (f.Shops ?? []).Select(s => new ShopInfo(
            s.Id,
            s.Kind,
            new LocalizedText(s.VendorNames, s.Id),
            new ShopPlace(
                s.Place.Kind,
                new LocalizedText(s.Place.Names, string.Empty),
                s.Place.MapPoints.Select(mp => new ShopMapPoint(
                    mp.X, mp.Y, mp.Map, mp.MapX, mp.MapY,
                    mp.Names == null ? null : new LocalizedText(mp.Names, string.Empty))).ToList()),
            new ShopCurrency(s.Currency.ItemId, new LocalizedText(s.Currency.Names, s.Currency.ItemId), s.Currency.Icon),
            s.Condition == null ? null : new LocalizedText(s.Condition, string.Empty),
            (s.Pool ?? []).Select(p => new ShopPoolEntry(p.Group, p.Weight, p.Chance)).ToList(),
            (s.Products ?? []).Select(pr => new ShopProduct(
                pr.ItemId, new LocalizedText(pr.Names, pr.ItemId), pr.Icon, pr.Price,
                pr.PerPurchase, pr.Stock, pr.OncePerPlayer, pr.PoolGroup, pr.Chance)).ToList(),
            s.CharacterNum,
            (s.Pals ?? []).Select(pa => new ShopPal(pa.PalId, pa.LevelMin, pa.LevelMax)).ToList())).ToList();
        return new GameData(meta, mechanics, pals, passives, actives, elements, workTypes, terms) { Relics = relics, Technologies = technologies, Shops = shops };
    }

    private static ObtainInfo MapObtain(ObtainDto o, string palId)
    {
        MapPoint Point(MapPointDto p) => new(p.X, p.Y, p.Radius, p.Map, p.MapX, p.MapY);
        return new ObtainInfo(
            o.Methods.ToList(),
            o.Wild.Select(w => new WildSpawn(w.Spawner, w.Region,
                w.RegionNames == null ? null : new LocalizedText(w.RegionNames, w.Region),
                w.LevelMin, w.LevelMax, w.Night, w.WorldTreeAura, w.Points.Select(Point).ToList())).ToList(),
            o.Bosses.Select(b => new BossSpawn(b.Kind, b.Spawner, b.Level, Point(b.Point))).ToList(),
            o.Dungeons.Select(d => new DungeonSpawn(d.Area, new LocalizedText(d.Names, d.Area), d.Rank, d.LevelMin, d.LevelMax)).ToList(),
            o.Cages.Select(c => new CageSpawn(c.Field, c.LevelMin, c.LevelMax, c.Weight)).ToList(),
            o.Merchants.Select(m => new MerchantEntry(m.Shop, m.LevelMin, m.LevelMax)).ToList(),
            o.Fishing.Select(f => new FishingEntry(f.Lottery, f.Group, f.LevelMin, f.LevelMax, f.Weight)).ToList(),
            o.Raids.Select(r => new RaidEntry(r.Summon, r.Level)).ToList(),
            o.TowerBoss);
    }

    private static Dictionary<int, int> ToIntKeys(IReadOnlyDictionary<string, int> source) =>
        source.ToDictionary(kv => int.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
}
