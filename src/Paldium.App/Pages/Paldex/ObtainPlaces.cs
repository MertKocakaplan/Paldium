using System.Globalization;
using System.Net;
using Microsoft.Extensions.Localization;
using Paldium.App.Resources;
using Paldium.App.Services;
using Paldium.Core.Domain;

namespace Paldium.App.Pages.PaldexParts;

/// <summary>One group of wild spawn areas: a region with its levels, night and aura marks, on one map.</summary>
public sealed record WildGroup(string Region, bool HasName, int LevelMin, int LevelMax, bool Night, bool Aura, int Areas, string? MapId, IReadOnlyList<MapPoint> Points);

/// <summary>
/// Where a species lives, in the texts of one language: the wild areas and bosses of the "Where in the world" section, the full screen viewer
/// (T104) and the Pals tab of the Map page (T146).
/// </summary>
public sealed class ObtainPlaces(IStringLocalizer<Strings> l, GameTerms terms, string lang)
{
    /// <summary>One group per region, levels, night, aura and map: a spawner with points on both maps counts on each.</summary>
    public List<WildGroup> Wild(ObtainInfo? obtain) => (obtain?.Wild ?? [])
        .SelectMany(w => w.Points.Select(pt => MapOf(pt.Map)).Distinct().DefaultIfEmpty().Select(map => (Spawn: w, Map: map)))
        .GroupBy(x => (Region: RegionName(x.Spawn), HasName: x.Spawn.RegionNames != null || l[$"Obtain.Region.{x.Spawn.Region}"] is { ResourceNotFound: false },
            x.Spawn.LevelMin, x.Spawn.LevelMax, x.Spawn.Night, x.Spawn.WorldTreeAura, x.Map))
        .Select(g => new WildGroup(g.Key.Region, g.Key.HasName, g.Key.LevelMin, g.Key.LevelMax, g.Key.Night, g.Key.WorldTreeAura,
            g.Select(x => x.Spawn.Spawner).Distinct(StringComparer.Ordinal).Count(), g.Key.Map,
            g.SelectMany(x => x.Spawn.Points).Where(pt => g.Key.Map != null && pt.Map == g.Key.Map).ToList()))
        .OrderBy(g => g.MapId == ObtainMaps.Main ? 0 : g.MapId == ObtainMaps.Tree ? 1 : 2)
        .ThenBy(g => g.Region, StringComparer.Create(CultureInfo.GetCultureInfo(lang), ignoreCase: true))
        .ThenBy(g => g.LevelMin)
        .ToList();

    /// <summary>The places list: the wild areas, then the Alphas and imprisoned Pals, Palpagos first.</summary>
    public List<MapPlace> Places(ObtainInfo? obtain)
    {
        var places = new List<MapPlace>();
        if (obtain == null) return places;
        foreach (var g in Wild(obtain))
        {
            // A region without a name shows its id.
            var name = g.Region;
            var title = name + (g.Night ? " · " + l["Obtain.Night"].Value : "") + (g.Aura ? " · " + l["Obtain.Aura"].Value : "");
            var displayName = name + (g.Aura ? " · " + l["Obtain.Aura"].Value : "");
            var meta = Levels(g.LevelMin, g.LevelMax) + " · " + l["Obtain.AreaCount", g.Areas].Value;
            places.Add(new MapPlace("wild", terms.ObtainMethod("Wild"), displayName, meta, title, g.MapId,
                g.Points.Select(pt => ObtainMaps.Dot(pt, alpha: false, night: g.Night)).ToList(), g.Night));
        }

        foreach (var b in obtain.Bosses.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Level))
        {
            var map = MapOf(b.Point.Map);
            var alpha = b.Kind == "Alpha";
            var name = BossPosition(b.Point);
            places.Add(new MapPlace(alpha ? "alpha" : "wild", alpha ? terms.T("Alpha") : terms.ObtainMethod("Imprisoned"), name, Levels(b.Level, b.Level), name, map,
                map != null ? [ObtainMaps.Dot(b.Point, alpha)] : []));
        }

        return ObtainMaps.Ordered(places);
    }

    public static string? MapOf(string? map) => map is ObtainMaps.Main or ObtainMaps.Tree ? map : null;

    public string RegionName(WildSpawn spawn) =>
        spawn.RegionNames?.Get(lang) ?? (l[$"Obtain.Region.{spawn.Region}"] is { ResourceNotFound: false } label ? label.Value : spawn.Region);

    public string Levels(int min, int max) =>
        min == 0 && max == 0
            ? "-"
            : min == max
                ? l["Detail.WildLevelSingle", min].Value
                : l["Detail.WildLevelRange", min, max].Value;

    public string BossPosition(MapPoint point) => point.Map switch
    {
        "MainMap" when point.MapX is { } mx && point.MapY is { } my => l["Detail.MainMapCoords", mx, my].Value,
        "MainMap" => l["Detail.MainMap"].Value,
        "Tree" => terms.T("Map.Tree"),
        _ => "-",
    };

    /// <summary>The text of a piece of markup (a name that may carry a span).</summary>
    public static string Plain(Microsoft.AspNetCore.Components.MarkupString markup) =>
        WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(markup.Value, "<[^>]+>", ""));
}
