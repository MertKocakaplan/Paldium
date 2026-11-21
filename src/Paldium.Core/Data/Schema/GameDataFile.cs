using System.Text.Json;

namespace Paldium.Core.Data.Schema;

// Schema DTOs for data/gamedata.json (ARCHITECTURE section 4.1).
// Localized maps are SortedDictionary (ordinal) so that serialization is deterministic.

public sealed record GameDataFile(
    MetaDto Meta,
    IReadOnlyList<string> Languages,
    MechanicsDto Mechanics,
    IReadOnlyList<ElementDto> Elements,
    IReadOnlyList<WorkTypeDto> WorkTypes,
    IReadOnlyList<PalDto> Pals,
    IReadOnlyList<PassiveDto> Passives,
    IReadOnlyList<ActiveDto> Actives,
    SortedDictionary<string, SortedDictionary<string, string>>? Terms = null,
    IReadOnlyList<RelicDto>? Relics = null,
    IReadOnlyList<TechnologyDto>? Technologies = null);

/// <summary>
/// T126: one Pal Effigy enhancement of the player (the statue of power): the game's type name, the effigy item, the texts and the cost and effect of every rank.
/// <c>Effect</c> is the total effect in percent at that rank (0 where the table has none: the capture power of the Lifmunk effigy).
/// </summary>
public sealed record RelicDto(
    string Type, int Index, string ItemId, SortedDictionary<string, string> ItemNames, SortedDictionary<string, string> Names,
    SortedDictionary<string, string> Descriptions, string? Icon, IReadOnlyList<RelicRankDto> Ranks);

public sealed record RelicRankDto(int Rank, int Required, double Effect);

/// <summary>
/// T128: one technology of the tech tree (DT_TechnologyRecipeUnlock): the level that unlocks it, its points (ancient points when <c>Ancient</c>), the game texts,
/// the prerequisites and the buildings and items it unlocks.
/// </summary>
public sealed record TechnologyDto(
    string Id, int Index, int Level, int Cost, bool Ancient, SortedDictionary<string, string> Names, SortedDictionary<string, string> Descriptions, string? Icon,
    TechnologyRequiresDto? Requires, IReadOnlyList<TechnologyUnlockDto> Unlocks);

public sealed record TechnologyRequiresDto(string? TechnologyId, TechnologyBossDto? TowerBoss, TechnologyResearchDto? Research);

public sealed record TechnologyBossDto(string Type, SortedDictionary<string, string> Names);

public sealed record TechnologyResearchDto(string Id, SortedDictionary<string, string> Names);

/// <summary><c>Kind</c> is "build" (a building) or "item".</summary>
public sealed record TechnologyUnlockDto(string Kind, string Id, SortedDictionary<string, string> Names, string? Icon);

public sealed record MetaDto(
    int SchemaVersion,
    string GameVersion,
    string GameBuild,
    string GeneratedAtUtc,
    IReadOnlyList<SourceDto> Sources);

public sealed record SourceDto(string Name, string Commit, string DbVersion);

public sealed record MechanicsDto(
    int MaxPassives,
    SortedDictionary<string, int> PassiveInheritWeights,
    SortedDictionary<string, int> PassiveRandomAddWeights,
    IReadOnlyList<int> TalentInheritWeights,
    double AlphaRate,
    MutationDto Mutation,
    IReadOnlyList<int> WildPassiveCountWeights,
    SortedDictionary<string, int> EggSizeMinRarity,
    IReadOnlyList<CakeDto> Cakes);

public sealed record MutationDto(
    double Rate,
    double RankCoefficient,
    double RankDiffPenalty,
    double RandomCoefficient,
    int MinTalent);

public sealed record CakeDto(
    string Id,
    int TalentBonusMin,
    int TalentBonusMax,
    int MutationRateBonusPercent,
    int CombiRankBonus,
    int BreedCount,
    bool InheritAllActiveSkills,
    int PassiveInheritCountOverride);

public sealed record ElementDto(string Id, SortedDictionary<string, string> Names, string Icon);

public sealed record WorkTypeDto(string Id, SortedDictionary<string, string> Names, string Icon);

public sealed record PalDto(
    string Id,
    int DexNo,
    bool IsVariant,
    string DexLabel,
    int TableOrder,
    SortedDictionary<string, string> Names,
    IReadOnlyList<string> Elements,
    int Rarity,
    string Size,
    bool Nocturnal,
    int Price,
    int BreedingPower,
    int BreedingPriority,
    double MaleProbability,
    StatsDto Stats,
    SortedDictionary<string, int> Work,
    LevelRangeDto? WildLevel,
    IReadOnlyList<LearnsetEntryDto> AttackLearnset,
    IReadOnlyList<string> GuaranteedPassives,
    EggDto Egg,
    string Icon,
    BreedingFlagsDto BreedingFlags,
    ObtainDto? Obtain,
    PartnerSkillDto? PartnerSkill = null,
    IReadOnlyList<DropDto>? Drops = null,
    bool IsUncapturable = false,
    IReadOnlyList<RanchRankDto>? Ranch = null,
    string? Group = null,
    MountDto? Mount = null);

/// <summary>
/// T124: a rideable Pal. <c>Kinds</c> are "Ground", "Flying" or "Water" (from the partner skill text); <c>SaddleLevel</c> is the level cap of the saddle's
/// technology; swim speeds are the game table's. <c>MoveByStar</c> / <c>SwimByStar</c> are the unconditional partner skill speed bonuses in percent for 0..4
/// stars; <c>Conditional</c> holds the bonuses that only apply under a condition (a night, a terrain, an element in the party).
/// </summary>
public sealed record MountDto(
    IReadOnlyList<string> Kinds, int? SaddleLevel, int SwimSpeed, int SwimDashSpeed,
    IReadOnlyList<int> MoveByStar, IReadOnlyList<int> SwimByStar, MountConditionalDto? Conditional = null);

public sealed record MountConditionalDto(IReadOnlyList<int> MoveByStar, IReadOnlyList<int> SwimByStar);

/// <summary>T108: what a Pal drops when assigned to the Ranch at one rank (1 to 10) of its Ranch work level; <c>Chance</c> is <c>Weight</c> over the total weight (0..1).</summary>
public sealed record RanchRankDto(int Rank, IReadOnlyList<RanchDropDto> Drops);

public sealed record RanchDropDto(
    string ItemId, SortedDictionary<string, string> Names, string? Icon, double Weight, double Chance, int Min, int Max);

/// <summary>Schema 2 (T33): how a species can be obtained other than breeding (ARCHITECTURE 4.3).</summary>
public sealed record ObtainDto(
    IReadOnlyList<string> Methods,
    IReadOnlyList<WildSpawnDto> Wild,
    IReadOnlyList<BossSpawnDto> Bosses,
    IReadOnlyList<DungeonSpawnDto> Dungeons,
    IReadOnlyList<CageDto> Cages,
    IReadOnlyList<MerchantDto> Merchants,
    IReadOnlyList<FishingDto> Fishing,
    IReadOnlyList<RaidDto> Raids,
    bool TowerBoss);

/// <summary>A world position; <c>Map</c> is "MainMap", "Tree" or null (outside both), <c>MapX/MapY</c> only for MainMap.</summary>
public sealed record MapPointDto(double X, double Y, int Radius, string? Map, int? MapX, int? MapY);

public sealed record WildSpawnDto(
    string Spawner, string Region, SortedDictionary<string, string>? RegionNames,
    int LevelMin, int LevelMax, bool Night, bool WorldTreeAura, IReadOnlyList<MapPointDto> Points);

public sealed record BossSpawnDto(string Kind, string Spawner, int Level, MapPointDto Point);

public sealed record DungeonSpawnDto(
    string Area, SortedDictionary<string, string> Names, string Rank, int LevelMin, int LevelMax);

public sealed record CageDto(string Field, int LevelMin, int LevelMax, double Weight);

public sealed record MerchantDto(string Shop, int LevelMin, int LevelMax);

public sealed record FishingDto(string Lottery, string Group, int LevelMin, int LevelMax, double Weight);

public sealed record RaidDto(string Summon, int Level);

/// <summary>Schema 2: partner skill texts (game wording; <c>{Variable}</c> placeholders are left as they are).</summary>
public sealed record PartnerSkillDto(
    SortedDictionary<string, string> Names,
    SortedDictionary<string, string> Descriptions,
    IReadOnlyList<IReadOnlyList<string>> PassivesByRank,
    IReadOnlyList<IReadOnlyList<string>>? PassiveIdsByRank = null,
    SortedDictionary<string, IReadOnlyList<double?>>? NumbersByRank = null,
    SortedDictionary<string, SortedDictionary<string, IReadOnlyList<string?>>>? TextsByRank = null);

/// <summary>Schema 2: base (level 0) drop of a species; <c>Rate</c> is 0..1.</summary>
public sealed record DropDto(
    string ItemId,
    SortedDictionary<string, string> Names,
    double Rate,
    int Min,
    int Max,
    int MinLevel);

/// <summary>Schema 2: one structural effect of a passive skill (DT_PassiveSkill_Main).</summary>
public sealed record EffectDto(string Type, double Value, string Target);

public sealed record StatsDto(
    int Hp,
    int Attack,
    int Defense,
    int WalkSpeed,
    int RunSpeed,
    int RideSprintSpeed,
    int TransportSpeed,
    int Stamina,
    int MaxFullStomach,
    int FoodAmount,
    int CraftSpeed);

public sealed record LevelRangeDto(int Min, int Max);

public sealed record LearnsetEntryDto(string SkillId, int Level);

public sealed record EggDto(string? Element, string Size);

/// <param name="Unbreedable">No pair of parents gives this species (it can only be obtained by other means).</param>
public sealed record BreedingFlagsDto(bool SameSpeciesOnly, int ParentPairCount, bool? UniqueRecipe, bool Unbreedable = false);

public sealed record PassiveDto(
    string Id,
    SortedDictionary<string, string> Names,
    SortedDictionary<string, string> Descriptions,
    int Rank,
    bool IsStandard,
    bool CanAppearRandomly,
    int RandomWeight,
    SurgeryDto Surgery,
    IReadOnlyList<string> Tags,
    bool MutationExclusive,
    IReadOnlyList<string>? PenaltyTags = null,
    IReadOnlyList<EffectDto>? Effects = null);

public sealed record SurgeryDto(bool Supported, int Cost, string? RequiredItem);

public sealed record ActiveDto(
    string Id,
    SortedDictionary<string, string> Names,
    string Element,
    int Power,
    double CooldownSeconds,
    bool CanInherit,
    bool HasSkillFruit);
