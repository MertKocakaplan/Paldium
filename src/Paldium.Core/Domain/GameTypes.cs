using System.Text.Json;
using Paldium.Core.Data;

namespace Paldium.Core.Domain;

public enum Gender { Male, Female }

public enum GenderReq { Any, Male, Female }

public sealed record PalStats(
    int Hp, int Attack, int Defense, int WalkSpeed, int RunSpeed, int RideSprintSpeed,
    int TransportSpeed, int Stamina, int MaxFullStomach, int FoodAmount, int CraftSpeed);

public sealed record LevelRange(int Min, int Max);

public sealed record LearnsetEntry(string SkillId, int Level);

public sealed record EggInfo(string? Element, string Size);

public sealed record BreedingFlags(bool SameSpeciesOnly, int ParentPairCount, bool? UniqueRecipe, bool Unbreedable = false);

/// <summary>Partner skill texts of a species (game wording).</summary>
public sealed record PartnerSkillInfo(
    LocalizedText Names, LocalizedText Descriptions, IReadOnlyList<IReadOnlyList<string>> PassivesByRank)
{
    /// <summary>Passive skill ids granted at each rank (PassiveSkills of the game row; empty for schema 1 data).</summary>
    public IReadOnlyList<IReadOnlyList<string>> PassiveIdsByRank { get; init; } = [];

    /// <summary>Value of each <c>{placeholder}</c> of the description by rank 1..5 (null: unknown for that rank).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<double?>> NumbersByRank { get; init; } =
        new Dictionary<string, IReadOnlyList<double?>>();

    /// <summary>Text of each <c>{ReferenceMsgId_...}</c> placeholder: placeholder, language, then rank 1..5.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string?>>> TextsByRank { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string?>>>();
}

/// <summary>What a Pal drops in the Ranch at one rank (T108); <c>Chance</c> is the share of one draw (0..1) and <c>IconPath</c> a bundle path (null: no icon).</summary>
public sealed record RanchDrop(string ItemId, LocalizedText Names, string? IconPath, double Weight, double Chance, int Min, int Max);

public sealed record RanchRank(int Rank, IReadOnlyList<RanchDrop> Drops);

/// <summary>Base drop of a species; <c>Rate</c> is 0..1.</summary>
public sealed record DropInfo(string ItemId, LocalizedText Names, double Rate, int Min, int Max, int MinLevel);

/// <summary>One structural effect of a passive skill (game effect type names).</summary>
public sealed record PassiveEffect(string Type, double Value, string Target);

/// <summary>How a species can be obtained other than breeding (ARCHITECTURE 4.3, T33).</summary>
public sealed record ObtainInfo(
    IReadOnlyList<string> Methods,
    IReadOnlyList<WildSpawn> Wild,
    IReadOnlyList<BossSpawn> Bosses,
    IReadOnlyList<DungeonSpawn> Dungeons,
    IReadOnlyList<CageSpawn> Cages,
    IReadOnlyList<MerchantEntry> Merchants,
    IReadOnlyList<FishingEntry> Fishing,
    IReadOnlyList<RaidEntry> Raids,
    bool TowerBoss)
{
    public bool HasMethod(string method) => Methods.Contains(method, StringComparer.Ordinal);
}

/// <summary>World position; <c>Map</c> is "MainMap", "Tree" or null; <c>MapX/MapY</c> (in-game map coordinates) only for MainMap.</summary>
public sealed record MapPoint(double X, double Y, int Radius, string? Map, int? MapX, int? MapY);

public sealed record WildSpawn(
    string Spawner, string Region, LocalizedText? RegionNames, int LevelMin, int LevelMax,
    bool Night, bool WorldTreeAura, IReadOnlyList<MapPoint> Points);

public sealed record BossSpawn(string Kind, string Spawner, int Level, MapPoint Point);

public sealed record DungeonSpawn(string Area, LocalizedText Names, string Rank, int LevelMin, int LevelMax);

public sealed record CageSpawn(string Field, int LevelMin, int LevelMax, double Weight);

public sealed record MerchantEntry(string Shop, int LevelMin, int LevelMax);

public sealed record FishingEntry(string Lottery, string Group, int LevelMin, int LevelMax, double Weight);

public sealed record RaidEntry(string Summon, int Level);

public sealed record SurgeryInfo(bool Supported, int Cost, string? RequiredItem);

/// <summary>Kinds of a mount: where it carries the rider.</summary>
public static class MountKind
{
    public const string Ground = "Ground", Flying = "Flying", Water = "Water";
}

/// <summary>
/// A rideable species (T124): its kinds, the level cap of its saddle's technology (null: unknown), its swim speeds and the speed bonuses (percent) its partner
/// skill gives by star (index 0..4). <see cref="Conditional"/> holds the bonuses that only apply under a condition (they are not added to the table).
/// </summary>
public sealed record MountInfo(
    IReadOnlyList<string> Kinds, int? SaddleLevel, int SwimSpeed, int SwimDashSpeed,
    IReadOnlyList<int> MoveByStar, IReadOnlyList<int> SwimByStar, MountConditional? Conditional = null)
{
    public bool Is(string kind) => Kinds.Contains(kind, StringComparer.Ordinal);

    /// <summary>The unconditional partner skill move-speed bonus (percent) at 0..4 stars.</summary>
    public int MoveBonus(int stars) => MoveByStar.Count == 0 ? 0 : MoveByStar[Math.Clamp(stars, 0, MoveByStar.Count - 1)];

    public int SwimBonus(int stars) => SwimByStar.Count == 0 ? 0 : SwimByStar[Math.Clamp(stars, 0, SwimByStar.Count - 1)];
}

public sealed record MountConditional(IReadOnlyList<int> MoveByStar, IReadOnlyList<int> SwimByStar);

public sealed record PalSpecies(
    string Id, int DexNo, bool IsVariant, string DexLabel, int TableOrder,
    LocalizedText Names, IReadOnlyList<string> Elements, int Rarity, string Size, bool Nocturnal,
    int BreedingPower, int BreedingPriority, double MaleProbability, PalStats Stats,
    IReadOnlyDictionary<string, int> Work, LevelRange? WildLevel, IReadOnlyList<LearnsetEntry> AttackLearnset,
    IReadOnlyList<string> GuaranteedPassives, EggInfo Egg, string IconPath, BreedingFlags BreedingFlags,
    PartnerSkillInfo? PartnerSkill, ObtainInfo? Obtain)
{
    /// <summary>Base price (kept outside the architecture record signature, from the data bundle).</summary>
    public int Price { get; init; }

    /// <summary>Base drops (schema 2; empty for schema 1 data).</summary>
    public IReadOnlyList<DropInfo> Drops { get; init; } = [];

    /// <summary>Group of related species the user can exclude as one (T107; "terraria": the Yakushima species), null for most.</summary>
    public string? Group { get; init; }

    /// <summary>Ranch drops by rank 1..10 (T108); empty for Pals that do not drop anything in the Ranch.</summary>
    public IReadOnlyList<RanchRank> Ranch { get; init; } = [];

    /// <summary>True for species the game marks as not capturable (schema 2).</summary>
    public bool IsUncapturable { get; init; }

    /// <summary>The mount data of a rideable species (T124); null for the others.</summary>
    public MountInfo? Mount { get; init; }
}

public sealed record PassiveSkill(
    string Id, LocalizedText Names, LocalizedText Descriptions, int Rank,
    bool IsStandard, bool CanAppearRandomly, int RandomWeight, SurgeryInfo Surgery,
    IReadOnlyList<string> Tags, IReadOnlyList<string> PenaltyTags, bool MutationExclusive)
{
    /// <summary>Structural effects from the game table (schema 2; empty for schema 1 data).</summary>
    public IReadOnlyList<PassiveEffect> Effects { get; init; } = [];
}

public sealed record ActiveSkill(
    string Id, LocalizedText Names, string Element, int Power,
    double CooldownSeconds, bool CanInherit, bool HasSkillFruit);

public sealed record ElementInfo(string Id, LocalizedText Names, string IconPath);

public sealed record WorkTypeInfo(string Id, LocalizedText Names, string IconPath);

public sealed record GameDataSource(string Name, string Commit, string DbVersion);

public sealed record GameDataMeta(
    int SchemaVersion, string GameVersion, string GameBuild, string GeneratedAtUtc,
    IReadOnlyList<GameDataSource> Sources);

public sealed record MutationConfig(
    double Rate, double RankCoefficient, double RankDiffPenalty, double RandomCoefficient, int MinTalent);

public sealed record CakeConfig(
    string Id, int TalentBonusMin, int TalentBonusMax, int MutationRateBonusPercent, int CombiRankBonus,
    int BreedCount, bool InheritAllActiveSkills, int PassiveInheritCountOverride);

/// <summary>Mechanics constants from the data bundle (they can change with game patches).</summary>
public sealed record MechanicsConfig(
    int MaxPassives,
    IReadOnlyDictionary<int, int> PassiveInheritWeights,
    IReadOnlyDictionary<int, int> PassiveRandomAddWeights,
    IReadOnlyList<int> TalentInheritWeights,
    double AlphaRate,
    MutationConfig Mutation,
    IReadOnlyList<int> WildPassiveCountWeights,
    IReadOnlyDictionary<string, int> EggSizeMinRarity,
    IReadOnlyList<CakeConfig> Cakes);

/// <summary>The cost and total effect (percent) of one rank of a Pal Effigy enhancement (T126).</summary>
public sealed record RelicRank(int Rank, int Required, double Effect);

/// <summary>
/// One Pal Effigy enhancement (T126): the type (<c>EPalRelicType</c> name), the effigy item with its names, the effect's name and description (game texts)
/// and the ranks. <see cref="Total"/> is the number of effigies that maxes it.
/// </summary>
public sealed record RelicInfo(
    string Type, int Index, string ItemId, LocalizedText ItemNames, LocalizedText Names, LocalizedText Descriptions, string? IconPath, IReadOnlyList<RelicRank> Ranks)
{
    public int Total => Ranks.Sum(r => r.Required);
}

/// <summary>A tower boss, research or technology a technology needs first (T128); the names are the game's texts.</summary>
public sealed record TechnologyRequirement(string? TechnologyId, string? TowerBossType, LocalizedText? TowerBossName, string? ResearchId, LocalizedText? ResearchName);

/// <summary>A building (<see cref="IsBuilding"/>) or an item a technology unlocks (T128).</summary>
public sealed record TechnologyUnlock(bool IsBuilding, string Id, LocalizedText Name, string? IconPath);

/// <summary>One technology of the tech tree (T128): level, points, ancient flag, game texts, prerequisites and unlocks. <see cref="Index"/> is the game table's order.</summary>
public sealed record TechnologyInfo(
    string Id, int Index, int Level, int Cost, bool Ancient, LocalizedText Names, LocalizedText Descriptions, string? IconPath,
    TechnologyRequirement? Requires, IReadOnlyList<TechnologyUnlock> Unlocks);
