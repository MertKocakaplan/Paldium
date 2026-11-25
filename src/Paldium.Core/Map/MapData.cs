using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Map;

public sealed record MapSurface(
    string Id,
    string Image,
    string LargeImage,
    MapProjection Projection);

public sealed record MapCategory(
    string Id,
    string Group,
    LocalizedText Names,
    string? Icon,
    bool DefaultOn);

public sealed record MapRegion(
    string Id,
    string Map,
    int Level,
    LocalizedText Names,
    double X,
    double Y,
    int? MapX,
    int? MapY,
    double? HalfX = null,
    double? HalfY = null,
    double? Radius = null);

public sealed record MapMarker(
    string Id,
    string Category,
    string Map,
    double X,
    double Y,
    int? MapX,
    int? MapY,
    LocalizedText? Names,
    string? RegionId,
    string? RefKind,
    string? RefId,
    string? Extra,
    IReadOnlyList<string>? Quests = null,
    IReadOnlyList<MapStack>? Items = null,
    LocalizedText? Hint = null,
    LocalizedText? Boss = null,
    double? Range = null);

/// <summary>A predator (rampaging) Pal (T149): the species it is a variant of and the game's name for the variant.</summary>
public sealed record MapPredator(string PalId, LocalizedText Names);

/// <summary>A step of a quest; <paramref name="Block"/> is the index of its block group, as a save counts the steps.</summary>
public sealed record MapStep(int Block, LocalizedText Names);

/// <summary>An item a marker or a quest names. <paramref name="Unlocks"/> is the product of a schematic.</summary>
public sealed record MapItem(string Id, LocalizedText Names, string? Icon, string? Unlocks);

public sealed record MapStack(string ItemId, int Count);

/// <summary>A main or side quest (T145); <paramref name="Points"/> are marker ids, the giver's first.</summary>
public sealed record MapQuest(
    string Id,
    bool IsMain,
    LocalizedText Names,
    LocalizedText? Description,
    int Blocks,
    IReadOnlyList<MapStep> Steps,
    int Exp,
    IReadOnlyList<MapStack> Rewards,
    IReadOnlyList<LocalizedText> Givers,
    IReadOnlyList<string> Points);

public sealed class MapData
{
    private readonly Dictionary<string, MapSurface> _maps;
    private readonly Dictionary<string, MapCategory> _categories;
    private readonly Dictionary<string, MapRegion> _regions;
    private readonly Dictionary<string, MapMarker> _pointsById;
    private readonly Dictionary<string, MapQuest> _quests;
    private readonly Dictionary<string, MapItem> _items;

    public MapData(
        IReadOnlyList<MapSurface> maps,
        IReadOnlyList<MapCategory> categories,
        IReadOnlyList<MapRegion> regions,
        IReadOnlyList<MapMarker> points,
        IReadOnlyList<MapQuest>? quests = null,
        IReadOnlyList<MapItem>? items = null,
        IReadOnlyList<MapPredator>? predators = null)
    {
        Predators = predators ?? [];
        Maps = maps;
        Categories = categories;
        Regions = regions;
        Points = points;
        _maps = maps.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        _categories = categories.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        _regions = regions.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        _pointsById = points.ToDictionary(p => p.Id, StringComparer.Ordinal);
        Quests = quests ?? [];
        _quests = Quests.ToDictionary(q => q.Id, StringComparer.OrdinalIgnoreCase);
        Items = items ?? [];
        _items = Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<MapSurface> Maps { get; }
    public IReadOnlyList<MapCategory> Categories { get; }
    public IReadOnlyList<MapRegion> Regions { get; }
    public IReadOnlyList<MapMarker> Points { get; }
    public IReadOnlyList<MapQuest> Quests { get; }
    public IReadOnlyList<MapItem> Items { get; }
    public IReadOnlyList<MapPredator> Predators { get; }

    /// <summary>
    /// The same map with markers the app adds while running (the player's bases, T151): their category is added when it has markers. An added
    /// marker gets the nearest named sub region of its map.
    /// </summary>
    public MapData With(MapCategory category, IReadOnlyList<MapMarker> added)
    {
        if (added.Count == 0) return this;
        var placed = added.Select(p => p.RegionId != null ? p : p with
        {
            RegionId = Regions.Where(r => r.Map == p.Map && r.Level > 0)
                .MinBy(r => (r.X - p.X) * (r.X - p.X) + (r.Y - p.Y) * (r.Y - p.Y))?.Id,
        }).ToList();
        // The added category stands with the other locations, before the first category of another group.
        var categories = Categories.ToList();
        var at = categories.FindLastIndex(c => c.Group == category.Group);
        categories.Insert(at < 0 ? 0 : at + 1, category);
        return new MapData(Maps, categories, Regions, [.. Points, .. placed], Quests, Items, Predators);
    }

    public MapQuest? TryGetQuest(string id) => _quests.TryGetValue(id, out var q) ? q : null;
    public MapItem? TryGetItem(string id) => _items.TryGetValue(id, out var i) ? i : null;

    public MapSurface? TryGetMap(string id) => _maps.TryGetValue(id, out var m) ? m : null;
    public MapCategory? TryGetCategory(string id) => _categories.TryGetValue(id, out var c) ? c : null;
    public MapRegion? TryGetRegion(string id) => _regions.TryGetValue(id, out var r) ? r : null;
    public MapMarker? TryGetPoint(string id) => _pointsById.TryGetValue(id, out var p) ? p : null;
}
