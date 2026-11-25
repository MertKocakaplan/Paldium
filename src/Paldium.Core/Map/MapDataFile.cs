using System.Text.Json.Serialization;

namespace Paldium.Core.Map;

// Compact JSON schema for data/map/map-points.json (< 4 MB constraint).
// Field names on points are short to keep the serialized payload small (~1.5-2.5 MB).

public sealed record MapDataFile(
    int SchemaVersion,
    IReadOnlyList<MapBoundsDto> Maps,
    IReadOnlyList<MapCategoryDto> Categories,
    IReadOnlyList<MapRegionDto> Regions,
    IReadOnlyList<MapMarkerDto> Points,
    IReadOnlyList<MapQuestDto>? Quests = null,
    SortedDictionary<string, MapItemDto>? Items = null,
    IReadOnlyList<MapPredatorDto>? Predators = null);

/// <summary>A predator (rampaging) Pal (T149): the species it is a variant of and the game's name for the variant.</summary>
public sealed record MapPredatorDto(string Pal, SortedDictionary<string, string> Names);

/// <summary>A step of a quest; <paramref name="Block"/> is the index of its block group, as a save counts the steps.</summary>
public sealed record MapStepDto(int Block, SortedDictionary<string, string> Names);

/// <summary>An item a marker or a quest names (T143, T145). <paramref name="Unlocks"/> is the product of a schematic.</summary>
public sealed record MapItemDto(SortedDictionary<string, string> Names, string? Icon, string? Unlocks = null);

public sealed record MapStackDto(string Id, int Count);

/// <summary>A main or side quest. <paramref name="Points"/> are marker ids: the giver's markers first, then the quest's places.</summary>
public sealed record MapQuestDto(
    string Id,
    string Type,
    SortedDictionary<string, string> Names,
    SortedDictionary<string, string> Descriptions,
    int Blocks,
    IReadOnlyList<MapStepDto> Steps,
    int Exp,
    IReadOnlyList<MapStackDto> Rewards,
    IReadOnlyList<SortedDictionary<string, string>> Givers,
    IReadOnlyList<string> Points);

public sealed record MapBoundsDto(
    string Id,
    string Image,
    string LargeImage,
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    bool HasGameCoords);

public sealed record MapCategoryDto(
    string Id,
    string Group,
    SortedDictionary<string, string> Names,
    string? Icon,
    bool DefaultOn);

public sealed record MapRegionDto(
    string Id,
    string Map,
    int Level,
    SortedDictionary<string, string> Names,
    double X,
    double Y,
    int? MapX = null,
    int? MapY = null,
    double? HalfX = null,
    double? HalfY = null,
    double? Radius = null);

public sealed record MapMarkerDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("c")] string Category,
    [property: JsonPropertyName("m")] string Map,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("mx")] int? MapX = null,
    [property: JsonPropertyName("my")] int? MapY = null,
    [property: JsonPropertyName("n")] SortedDictionary<string, string>? Names = null,
    [property: JsonPropertyName("r")] string? RegionId = null,
    [property: JsonPropertyName("rk")] string? RefKind = null,
    [property: JsonPropertyName("ri")] string? RefId = null,
    [property: JsonPropertyName("ex")] string? Extra = null,
    [property: JsonPropertyName("q"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Quests = null,
    [property: JsonPropertyName("it"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MapStackDto>? Items = null,
    [property: JsonPropertyName("h"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SortedDictionary<string, string>? Hint = null,
    [property: JsonPropertyName("bn"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SortedDictionary<string, string>? Boss = null);
