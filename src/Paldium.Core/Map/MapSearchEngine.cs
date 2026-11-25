using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.Core.Map;

public enum MapSearchKind
{
    Region,
    FastTravel,
    Dungeon,
    Merchant,
    Tower,
    Effigy,
    Quest,
    Predator,
    Base,
    Enemy,
    Pal,
}

public sealed record MapSearchHit(
    MapSearchKind Kind,
    string Id,
    string PrimaryText,
    string? SecondaryText,
    string MapId,
    double X,
    double Y,
    string? PointId,
    string? CategoryId,
    string? PalId = null,
    int RankOrder = 0);

/// <summary>Pure bilingual map search across regions, markers and Pal species (case &amp; Turkish diacritic insensitive).</summary>
public static class MapSearchEngine
{
    private static readonly string[] SearchLanguages = ["en", "tr"];

    public static IReadOnlyList<MapSearchHit> Search(
        string? query,
        string language,
        string activeMapId,
        MapData? mapData,
        IGameData? gameData,
        int maxPerGroup = 12)
    {
        var foldedQuery = TextFold.Fold(query?.Trim());
        if (string.IsNullOrEmpty(foldedQuery)) return [];

        var hits = new List<MapSearchHit>();

        if (mapData != null)
        {
            foreach (var region in mapData.Regions)
            {
                var score = MatchScore(region.Names, foldedQuery);
                if (score == int.MaxValue) continue;
                hits.Add(new MapSearchHit(
                    MapSearchKind.Region,
                    region.Id,
                    region.Names.Get(language),
                    null,
                    region.Map,
                    region.X,
                    region.Y,
                    PointId: null,
                    CategoryId: null,
                    RankOrder: score));
            }

            foreach (var pt in mapData.Points)
            {
                var kind = ClassifyCategory(pt.Category);
                if (kind == null || pt.Names == null) continue;
                // A tower is also found by its boss.
                var score = Math.Min(MatchScore(pt.Names, foldedQuery), pt.Boss == null ? int.MaxValue : MatchScore(pt.Boss, foldedQuery));
                if (score == int.MaxValue) continue;
                var regName = pt.RegionId != null ? mapData.TryGetRegion(pt.RegionId)?.Names.Get(language) : null;
                hits.Add(new MapSearchHit(
                    kind.Value,
                    pt.Id,
                    pt.Names.Get(language),
                    regName,
                    pt.Map,
                    pt.X,
                    pt.Y,
                    PointId: pt.Id,
                    CategoryId: pt.Category,
                    RankOrder: score));
            }
        }

        if (mapData != null)
        {
            foreach (var quest in mapData.Quests)
            {
                var score = MatchScore(quest.Names, foldedQuery);
                if (score == int.MaxValue) continue;
                var place = quest.Points.Select(mapData.TryGetPoint).FirstOrDefault(p => p != null);
                hits.Add(new MapSearchHit(
                    MapSearchKind.Quest, quest.Id, quest.Names.Get(language),
                    place?.RegionId != null ? mapData.TryGetRegion(place.RegionId)?.Names.Get(language) : null,
                    place?.Map ?? activeMapId, place?.X ?? 0, place?.Y ?? 0, PointId: place?.Id, CategoryId: place?.Category, RankOrder: score));
            }

            // Ancient towers are found by what lies in them: the schematic and the item it unlocks (the Dog Coins are in every tower).
            foreach (var pt in mapData.Points.Where(p => p.Category == "pickup_tower" && p.Items != null))
            {
                var best = int.MaxValue;
                foreach (var stack in pt.Items!.Where(s => !string.Equals(s.ItemId, "DogCoin", StringComparison.OrdinalIgnoreCase)))
                {
                    if (mapData.TryGetItem(stack.ItemId) is not { } item) continue;
                    best = Math.Min(best, MatchScore(item.Names, foldedQuery));
                    if (item.Unlocks != null && mapData.TryGetItem(item.Unlocks) is { } product) best = Math.Min(best, MatchScore(product.Names, foldedQuery));
                }
                if (best == int.MaxValue || mapData.TryGetItem(pt.Items![0].ItemId) is not { } first) continue;
                hits.Add(new MapSearchHit(
                    MapSearchKind.Tower, pt.Id, first.Names.Get(language),
                    pt.RegionId != null ? mapData.TryGetRegion(pt.RegionId)?.Names.Get(language) : null,
                    pt.Map, pt.X, pt.Y, PointId: pt.Id, CategoryId: pt.Category, RankOrder: best));
            }
        }

        // A predator opens its layer (T149).
        if (mapData != null && gameData != null)
        {
            foreach (var predator in mapData.Predators)
            {
                var score = MatchScore(predator.Names, foldedQuery);
                if (score == int.MaxValue) continue;
                var points = gameData.Pals.FirstOrDefault(p => p.Id == predator.PalId)?.Obtain?.Wild.SelectMany(w => w.Points).ToList() ?? [];
                if (points.Count == 0) continue;
                var map = points.Any(p => MatchesMap(p.Map, activeMapId)) ? activeMapId : points[0].Map ?? "MainMap";
                hits.Add(new MapSearchHit(MapSearchKind.Predator, predator.PalId, predator.Names.Get(language), null, map, 0, 0,
                    PointId: null, CategoryId: "predator:" + predator.PalId, RankOrder: score));
            }
        }

        // An effigy type opens its layer (T142): "Pengullet Effigy", "Lifmunk".
        if (mapData != null && gameData != null)
        {
            foreach (var relic in gameData.Relics)
            {
                var score = MatchScore(relic.ItemNames, foldedQuery);
                if (score == int.MaxValue) continue;
                var places = mapData.Points.Where(p => p.Category == "effigy" && p.Extra == relic.Type).ToList();
                if (places.Count == 0) continue;
                var map = places.Any(p => MatchesMap(p.Map, activeMapId)) ? activeMapId : places[0].Map;
                hits.Add(new MapSearchHit(
                    MapSearchKind.Effigy, relic.Type, relic.ItemNames.Get(language), null, map, 0, 0,
                    PointId: null, CategoryId: "effigy:" + relic.Type, RankOrder: score));
            }
        }

        if (gameData != null)
        {
            foreach (var pal in gameData.Pals)
            {
                var score = MatchScore(pal.Names, foldedQuery);
                if (score == int.MaxValue) continue;
                if (pal.Obtain is null) continue;

                // The map of the wild areas (the shown one first); a species without wild areas goes to the map of its Alpha.
                bool Wild(string map) => pal.Obtain.Wild.Any(w => w.Points.Any(p => MatchesMap(p.Map, map)));
                bool Boss(string map) => pal.Obtain.Bosses.Any(b => MatchesMap(b.Point.Map, map));
                string[] maps = [activeMapId, "MainMap", "Tree"];
                var targetMap = maps.FirstOrDefault(Wild) ?? maps.FirstOrDefault(Boss);
                double tx = 0, ty = 0;

                if (targetMap == null) continue;
                var firstPt = pal.Obtain.Wild.SelectMany(w => w.Points).FirstOrDefault(p => MatchesMap(p.Map, targetMap))
                              ?? pal.Obtain.Bosses.Select(b => b.Point).FirstOrDefault(p => MatchesMap(p.Map, targetMap));
                if (firstPt != null) { tx = firstPt.X; ty = firstPt.Y; }

                hits.Add(new MapSearchHit(
                    MapSearchKind.Pal,
                    pal.Id,
                    pal.Names.Get(language),
                    $"#{pal.DexLabel}",
                    targetMap,
                    tx,
                    ty,
                    PointId: null,
                    CategoryId: null,
                    PalId: pal.Id,
                    RankOrder: score));
            }
        }

        // The same character can have an NPC and a quest giver marker: one hit.
        var characters = new HashSet<(string, int, int)>();
        return hits
            .Where(h => h.Kind != MapSearchKind.Merchant || characters.Add((h.PrimaryText, (int)(h.X / 500), (int)(h.Y / 500))))
            .GroupBy(h => h.Kind)
            .OrderBy(g => g.Key)
            .SelectMany(g => g
                .OrderBy(h => h.RankOrder)
                .ThenByDescending(h => string.Equals(h.MapId, activeMapId, StringComparison.OrdinalIgnoreCase))
                .ThenBy(h => h.PrimaryText, StringComparer.CurrentCultureIgnoreCase)
                .Take(maxPerGroup))
            .ToList();
    }

    private static bool MatchesMap(string? pointMap, string targetMap)
    {
        if (string.Equals(targetMap, "Tree", StringComparison.OrdinalIgnoreCase))
            return string.Equals(pointMap, "Tree", StringComparison.OrdinalIgnoreCase);
        return string.Equals(pointMap, "MainMap", StringComparison.OrdinalIgnoreCase);
    }

    private static MapSearchKind? ClassifyCategory(string cat) => cat switch
    {
        "fast_travel" or "watchtower" => MapSearchKind.FastTravel,
        "base" => MapSearchKind.Base,
        "dungeon" or "sealed_realm" or "boss_tower" or "oil_rig" or "arena" or "respawn" => MapSearchKind.Dungeon,
        // Quests are found in the quest list, not by their markers.
        "merchant" or "npc" or "quest_giver" => MapSearchKind.Merchant,
        "alpha" or "bounty" or "enemy_camp" => MapSearchKind.Enemy,
        _ => null,
    };

    private static int MatchScore(LocalizedText text, string foldedQuery)
    {
        var best = int.MaxValue;
        foreach (var lang in SearchLanguages)
        {
            if (!text.TryGet(lang, out var raw)) continue;
            var folded = TextFold.Fold(raw);
            if (folded.Equals(foldedQuery, StringComparison.Ordinal)) best = Math.Min(best, 0);
            else if (folded.StartsWith(foldedQuery, StringComparison.Ordinal)) best = Math.Min(best, 1);
            else if (folded.Contains(foldedQuery, StringComparison.Ordinal)) best = Math.Min(best, 2);
        }
        return best;
    }
}
