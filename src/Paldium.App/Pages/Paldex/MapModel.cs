using System.Globalization;

namespace Paldium.App.Pages.PaldexParts;

/// <summary>
/// A point on one of the two maps: fractions 0..1 from the left and top of the picture, the spawn radius as a fraction of the map width.
/// <paramref name="Night"/>: the spawn area is only used at night (obtain.wild[].night; T116).
/// </summary>
public sealed record MapDot(double Left, double Top, double Radius, bool Alpha, int? MapX, int? MapY, bool Night = false);

/// <summary>
/// One row of the places list (T104): where a species lives. <see cref="Kind"/> is "wild" or "alpha"; <see cref="MapId"/> is the map it is on
/// ("MainMap" or "Tree", T140), or null for a place on neither picture (it stays in the list and cannot be selected).
/// </summary>
public sealed record MapPlace(string Kind, string KindLabel, string Name, string Meta, string Title, string? MapId, IReadOnlyList<MapDot> Dots, bool Night = false);

/// <summary>T140: the two maps of the "Where in the world" section.</summary>
public static class ObtainMaps
{
    public const string Main = "MainMap";
    public const string Tree = "Tree";

    private static readonly string[] Both = [Main, Tree];

    private static bool HasWild(Paldium.Core.Domain.ObtainInfo obtain, string map) =>
        obtain.Wild.Any(w => w.Points.Any(p => p.Map == map));

    private static bool HasBoss(Paldium.Core.Domain.ObtainInfo obtain, string map) => obtain.Bosses.Any(b => b.Point.Map == map);

    /// <summary>The maps with a wild area or a boss of the species, Palpagos first.</summary>
    public static IReadOnlyList<string> Of(Paldium.Core.Domain.ObtainInfo obtain) =>
        Both.Where(m => HasWild(obtain, m) || HasBoss(obtain, m)).ToList();

    /// <summary>The map shown first: the one with wild areas (Palpagos first); a species without wild areas shows the map of its boss.</summary>
    public static string Default(Paldium.Core.Domain.ObtainInfo obtain) =>
        Both.FirstOrDefault(m => HasWild(obtain, m)) ?? Both.FirstOrDefault(m => HasBoss(obtain, m)) ?? Main;

    /// <summary>The wild areas (spawners) on the map; a map with bosses only counts those.</summary>
    public static int AreaCount(Paldium.Core.Domain.ObtainInfo obtain, string map)
    {
        var wild = obtain.Wild.Where(w => w.Points.Any(p => p.Map == map)).Select(w => w.Spawner).Distinct(StringComparer.Ordinal).Count();
        return wild > 0 ? wild : obtain.Bosses.Count(b => b.Point.Map == map);
    }

    /// <summary>Palpagos places first, then the World Tree, then the places without a map; the order inside a map is kept.</summary>
    public static List<MapPlace> Ordered(IEnumerable<MapPlace> places) =>
        places.OrderBy(p => p.MapId == Main ? 0 : p.MapId == Tree ? 1 : 2).ToList();

    /// <summary>The dot of a world point on the picture of its own map.</summary>
    public static MapDot Dot(Paldium.Core.Domain.MapPoint point, bool alpha, bool night = false)
    {
        var projection = Paldium.Core.Domain.MapProjection.ForMap(point.Map);
        var (left, top) = projection.ToFraction(point.X, point.Y);
        return new MapDot(left, top, point.Radius / (projection.MaxY - projection.MinY), alpha, point.MapX, point.MapY, night);
    }
}

/// <summary>The part of the picture a preview shows (fractions of the picture) and the CSS that places the picture and the points in it.</summary>
public sealed record MapFrame(double Left, double Top, double Width, double Height)
{
    public const double Aspect = 1.6;

    /// <summary>Style of the picture inside the 16:10 window.</summary>
    public string ImageStyle => string.Create(CultureInfo.InvariantCulture,
        $"width:{100 / Width:0.##}%;height:{100 / Height:0.##}%;left:{-Left / Width * 100:0.##}%;top:{-Top / Height * 100:0.##}%");

    public string PointStyle(MapDot dot) => string.Create(CultureInfo.InvariantCulture,
        $"left:{(dot.Left - Left) / Width * 100:0.##}%;top:{(dot.Top - Top) / Height * 100:0.##}%");
}

public static class MapFraming
{
    /// <summary>Margin on every side, as a fraction of the map width.</summary>
    public const double Padding = 0.046;

    /// <summary>The preview is never narrower than this part of the map.</summary>
    public const double MinWidth = 0.36;

    /// <summary>
    /// The 16:10 window around the points: the points' box plus <see cref="Padding"/> each way, at least <see cref="MinWidth"/> wide, inside the
    /// map (planner of paldex.md "Where in the world").
    /// </summary>
    public static MapFrame Compute(IReadOnlyList<MapDot> dots)
    {
        if (dots.Count == 0) return new MapFrame(0, 0, 1, 1 / MapFrame.Aspect);
        double minX = dots.Min(d => d.Left), maxX = dots.Max(d => d.Left), minY = dots.Min(d => d.Top), maxY = dots.Max(d => d.Top);
        var width = Math.Clamp(Math.Max(maxX - minX + 2 * Padding, (maxY - minY + 2 * Padding) * MapFrame.Aspect), MinWidth, 1.0);
        var height = width / MapFrame.Aspect;
        var left = Math.Clamp((minX + maxX) / 2 - width / 2, 0, 1 - width);
        var top = Math.Clamp((minY + maxY) / 2 - height / 2, 0, 1 - height);
        return new MapFrame(left, top, width, height);
    }
}

/// <summary>The dots of a species' wild spawn areas for the preview and the night marks (T116).</summary>
public static class WildDots
{
    /// <summary>Every spawn area once (points close together share a dot; a night dot and a day dot at one place stay two), with the night flag.</summary>
    public static List<MapDot> Preview(IEnumerable<Paldium.Core.Domain.WildSpawn> wild, Func<Paldium.Core.Domain.MapPoint, bool, MapDot> dotOf, out int mapPoints,
        string map = ObtainMaps.Main)
    {
        var dots = new List<MapDot>();
        var seen = new HashSet<(int X, int Y, bool Night)>();
        mapPoints = 0;
        foreach (var w in wild)
        {
            foreach (var pt in w.Points)
            {
                if (!string.Equals(pt.Map, map, StringComparison.Ordinal)) continue;
                mapPoints++;
                var dot = dotOf(pt, w.Night);
                if (seen.Add(((int)Math.Round(dot.Left * 200), (int)Math.Round(dot.Top * 200), w.Night))) dots.Add(dot);
            }
        }

        return dots;
    }

    /// <summary>True when the species has wild spawn areas and every one of them is a night-only one.</summary>
    public static bool AllNight(IReadOnlyCollection<Paldium.Core.Domain.WildSpawn> wild) => wild.Count > 0 && wild.All(w => w.Night);
}
