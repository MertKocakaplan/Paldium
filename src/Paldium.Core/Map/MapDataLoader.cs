using System.Text.Json;
using Paldium.Core.Data;
using Paldium.Core.Data.Schema;
using Paldium.Core.Domain;

namespace Paldium.Core.Map;

/// <summary>Loads <c>data/map/map-points.json</c> cleanly, returning a structured failure when absent or corrupt.</summary>
public static class MapDataLoader
{
    public const string RelativePath = "map/map-points.json";

    public static MapData Load(string dataFolder) =>
        TryLoad(dataFolder, out var data, out var error) && data != null
            ? data
            : throw new InvalidDataException(error ?? "Map data could not be loaded.");

    private static List<MapStack>? Stacks(IReadOnlyList<MapStackDto>? stacks) => stacks?.Select(s => new MapStack(s.Id, s.Count)).ToList();

    public static bool TryLoad(string dataFolder, out MapData? data, out string? error)
    {
        data = null;
        error = null;

        var path = Path.Combine(dataFolder, "map", "map-points.json");
        if (!File.Exists(path))
        {
            error = $"Map data file not found: {path}";
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var file = JsonSerializer.Deserialize<MapDataFile>(bytes, SchemaJson.Compact);
            if (file == null || file.Maps.Count == 0)
            {
                error = $"Map data file is empty: {path}";
                return false;
            }

            var maps = file.Maps.Select(m =>
            {
                var proj = m.HasGameCoords && string.Equals(m.Id, "MainMap", StringComparison.OrdinalIgnoreCase)
                    ? MapProjection.Main with { MinX = m.MinX, MaxX = m.MaxX, MinY = m.MinY, MaxY = m.MaxY }
                    : new MapProjection(m.Id, m.MinX, m.MaxX, m.MinY, m.MaxY, HasGameCoordinates: m.HasGameCoords);
                return new MapSurface(m.Id, m.Image, m.LargeImage, proj);
            }).ToList();

            var categories = file.Categories.Select(c =>
                new MapCategory(c.Id, c.Group, new LocalizedText(c.Names, c.Id), c.Icon, c.DefaultOn)).ToList();

            var regions = file.Regions.Select(r =>
                new MapRegion(r.Id, r.Map, r.Level, new LocalizedText(r.Names, r.Id), r.X, r.Y, r.MapX, r.MapY, r.HalfX, r.HalfY, r.Radius)).ToList();

            var points = file.Points.Select(p =>
                new MapMarker(
                    p.Id, p.Category, p.Map, p.X, p.Y, p.MapX, p.MapY,
                    p.Names == null ? null : new LocalizedText(p.Names, p.Id),
                    p.RegionId, p.RefKind, p.RefId, p.Extra, p.Quests, Stacks(p.Items),
                    p.Hint == null ? null : new LocalizedText(p.Hint, p.Id),
                    p.Boss == null ? null : new LocalizedText(p.Boss, p.Id))).ToList();

            var quests = file.Quests?.Select(q => new MapQuest(
                q.Id, q.Type == "Main", new LocalizedText(q.Names, q.Id),
                q.Descriptions.Count == 0 ? null : new LocalizedText(q.Descriptions, q.Id),
                q.Blocks, q.Steps.Select(s => new MapStep(s.Block, new LocalizedText(s.Names, q.Id))).ToList(), q.Exp, Stacks(q.Rewards) ?? [],
                q.Givers.Select(g => new LocalizedText(g, q.Id)).ToList(), q.Points)).ToList();
            var items = file.Items?.Select(i => new MapItem(i.Key, new LocalizedText(i.Value.Names, i.Key), i.Value.Icon, i.Value.Unlocks)).ToList();

            var predators = file.Predators?.Select(p => new MapPredator(p.Pal, new LocalizedText(p.Names, p.Pal))).ToList();

            data = new MapData(maps, categories, regions, points, quests, items, predators);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
        {
            error = $"Map data could not be loaded: {ex.Message}";
            return false;
        }
    }
}
