using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Map;
using Paldium.Core.Owned;
using Paldium.Core.Queries;

namespace Paldium.App.Pages.MapParts;

/// <summary>
/// One switch of the layer list: a category, one effigy type of the effigy category (T142) or one species of the predator category (T149).
/// </summary>
public sealed record MapLayer(string Id, MapCategory Category, RelicInfo? Relic = null, MapPredator? Predator = null);

/// <summary>
/// What marking a marker means: an object is collected (effigies, ancient towers, journals), a place is opened (fast travel points,
/// watchtowers) or a tower boss is defeated (T144, T148).
/// </summary>
public enum MarkKind
{
    None,
    Collect,
    Open,
    Defeat,
}

/// <summary>A quest's state from the player's record (T150).</summary>
public enum QuestStatus
{
    Unknown,
    NotTaken,
    Active,
    Done,
}

public enum QuestFilter
{
    All,
    Active,
    NotTaken,
    Done,
}

/// <summary>A row of the Quests tab (T145). <see cref="Point"/> is the marker the map flies to; null when the quest has no place.</summary>
public sealed record QuestRow(MapQuest Quest, string Name, string? Giver, string? Region, MapMarker? Point, QuestStatus Status, int Step);

/// <summary>A spawn area where a predator may show up (T149): a wild area of its base species.</summary>
public sealed record PredatorArea(MapPredator Predator, MapPoint Point, int LevelMin, int LevelMax);

public static partial class WorldMapModel
{
    public const string Effigy = "effigy";
    public const string Tower = "pickup_tower";
    public const string Predator = "predator";
    public const string Base = "base";

    /// <summary>The layer set of the saved choice: a choice saved earlier does not know the categories added since.</summary>
    public const int LayerSet = 3;

    // category -> the layer set that added it
    private static readonly Dictionary<string, int> AddedIn = new(StringComparer.Ordinal)
    {
        [Tower] = 2, ["quest_giver"] = 2, ["oil_rig"] = 2, ["hexolite"] = 2, ["quest"] = 2, ["watchtower"] = 3,
    };

    public static string LayerId(MapMarker point) => point.Category == Effigy ? Effigy + ":" + point.Extra : point.Category;

    /// <summary>
    /// The switches in the data's order; the effigy category as one per type that has markers (the order of the statue's menu), the predator
    /// category as one per species.
    /// </summary>
    public static IReadOnlyList<MapLayer> Layers(MapData data, IReadOnlyList<RelicInfo> relics)
    {
        var layers = new List<MapLayer>();
        foreach (var category in data.Categories)
        {
            if (category.Id == Effigy)
            {
                var types = data.Points.Where(p => p.Category == Effigy && p.Extra != null).Select(p => p.Extra!).Distinct(StringComparer.Ordinal);
                layers.AddRange(types.Select(type => (Type: type, Relic: relics.FirstOrDefault(r => r.Type == type)))
                    .OrderBy(t => t.Relic?.Index ?? int.MaxValue).ThenBy(t => t.Type, StringComparer.Ordinal)
                    .Select(t => new MapLayer(Effigy + ":" + t.Type, category, t.Relic)));
            }
            else if (category.Id == Predator)
            {
                layers.AddRange(data.Predators.Select(p => new MapLayer(Predator + ":" + p.PalId, category, Predator: p)));
            }
            else
            {
                layers.Add(new MapLayer(category.Id, category));
            }
        }

        return layers;
    }

    /// <summary>
    /// The layers that are on: the saved choice, or the data's defaults. A saved category id ("effigy") means all of its switches. The player's
    /// bases are on unless they were switched off.
    /// </summary>
    public static IReadOnlySet<string> Enabled(IReadOnlyList<string>? saved, IReadOnlyList<MapLayer> layers, int savedSet = LayerSet, bool hideBases = false)
    {
        bool On(MapLayer layer) => layer.Category.Id == Base
            ? !hideBases
            : saved == null
                ? layer.Category.DefaultOn
                : saved.Contains(layer.Id) || saved.Contains(layer.Category.Id) ||
                  layer.Category.DefaultOn && AddedIn.TryGetValue(layer.Category.Id, out var added) && savedSet < added;
        return layers.Where(On).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The markers of a map per layer id and per category id; predators count their spawn areas.</summary>
    public static IReadOnlyDictionary<string, int> Counts(IEnumerable<MapMarker> points, string mapId)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var point in points.Where(p => p.Map == mapId))
        {
            counts[point.Category] = counts.GetValueOrDefault(point.Category) + 1;
            if (point.Category == Effigy) counts[LayerId(point)] = counts.GetValueOrDefault(LayerId(point)) + 1;
        }

        return counts;
    }

    /// <summary>The species an effigy shows: the effigy item is named after it ("Lamball Effigy"); null when no Pal has that name.</summary>
    public static PalSpecies? EffigyPal(RelicInfo relic, IReadOnlyList<PalSpecies> pals)
    {
        if (!relic.ItemNames.TryGet("en", out var item)) return null;
        return pals.FirstOrDefault(p => p.Names.TryGet("en", out var name) && string.Equals(name + " Effigy", item, StringComparison.OrdinalIgnoreCase));
    }

    // ---- collected, opened, defeated (T144, T148) ---------------------------------------------------------

    public static MarkKind KindOf(string category) => category switch
    {
        Effigy or Tower or "journal" => MarkKind.Collect,
        "fast_travel" or "watchtower" => MarkKind.Open,
        "boss_tower" => MarkKind.Defeat,
        _ => MarkKind.None,
    };

    /// <summary>
    /// True when the player's record says so: by the marker's object id (a tower also by its pickup row), a journal by its row name, a tower
    /// boss by its key.
    /// </summary>
    public static bool DoneInRecord(MapMarker point, WorldProgress? progress)
    {
        if (progress == null) return false;
        return point.Category switch
        {
            "journal" => point.Extra != null && progress.Notes.Contains(point.Extra),
            "boss_tower" => point.RefId != null && progress.Towers.Contains(point.RefId),
            Tower => progress.Objects.Contains(CaptureRecord.ObjectKey(point.Id)) ||
                     point.Extra != null && progress.Objects.Contains(CaptureRecord.ObjectKey(point.Extra)),
            Effigy or "fast_travel" or "watchtower" => progress.Objects.Contains(CaptureRecord.ObjectKey(point.Id)),
            _ => false,
        };
    }

    /// <summary>The markers that count as done: the record says so, or the user marked them.</summary>
    public static HashSet<string> Done(IEnumerable<MapMarker> points, IReadOnlyCollection<string> marked, WorldProgress? progress)
    {
        var mine = marked as IReadOnlySet<string> ?? marked.ToHashSet(StringComparer.Ordinal);
        return points.Where(p => KindOf(p.Category) != MarkKind.None && (mine.Contains(p.Id) || DoneInRecord(p, progress)))
            .Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The done markers of a map per layer id and per category.</summary>
    public static IReadOnlyDictionary<string, int> DoneCounts(IEnumerable<MapMarker> points, string mapId, IReadOnlySet<string> done) =>
        Counts(points.Where(p => done.Contains(p.Id)), mapId);

    /// <summary>The key of the personal marks: the world of the first source that knows its world, else "default".</summary>
    public static string WorldKey(IEnumerable<SourceStatus> sources) =>
        sources.Select(s => s.WorldId).FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? "default";

    /// <summary>The sources that read the world of that key; without a known world, the sources that name none.</summary>
    public static IReadOnlyList<string> SourcesOfWorld(IEnumerable<SourceStatus> sources, string worldKey) => sources
        .Where(s => string.IsNullOrEmpty(s.WorldId) ? worldKey == "default" : string.Equals(s.WorldId, worldKey, StringComparison.OrdinalIgnoreCase))
        .Select(s => s.SourceId).ToList();

    public static IReadOnlyList<string> Marked(IReadOnlyDictionary<string, IReadOnlyList<string>> collected, string worldKey) =>
        collected.TryGetValue(worldKey, out var ids) ? ids : [];

    /// <summary>The marks with the marker added to, or taken off, the world's set.</summary>
    public static Dictionary<string, IReadOnlyList<string>> Toggle(IReadOnlyDictionary<string, IReadOnlyList<string>> collected, string worldKey, string pointId)
    {
        var result = collected.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var ids = Marked(collected, worldKey).ToList();
        if (!ids.Remove(pointId)) ids.Add(pointId);
        if (ids.Count == 0) result.Remove(worldKey);
        else result[worldKey] = ids.OrderBy(i => i, StringComparer.Ordinal).ToList();
        return result;
    }

    // ---- quests (T145, T150) ------------------------------------------------------------------------------

    public static QuestStatus Status(string questId, QuestProgress? progress) =>
        progress == null ? QuestStatus.Unknown
        : progress.Done.Contains(questId) ? QuestStatus.Done
        : progress.Active.ContainsKey(questId) ? QuestStatus.Active
        : QuestStatus.NotTaken;

    /// <summary>The step an active quest is at, counted from 1 and never past its last step.</summary>
    public static int Step(MapQuest quest, QuestProgress? progress) =>
        progress != null && progress.Active.TryGetValue(quest.Id, out var block) ? Math.Clamp(block + 1, 1, Math.Max(1, quest.Blocks)) : 0;

    /// <summary>The step text that is current: the last one at or before the block the player is at.</summary>
    public static MapStep? CurrentStep(MapQuest quest, QuestProgress? progress)
    {
        var step = Step(quest, progress);
        return step == 0 ? null : quest.Steps.LastOrDefault(s => s.Block < step) ?? quest.Steps.FirstOrDefault();
    }

    /// <summary>The rows of one quest group, in the game's order; the filter looks at the name and the giver in both languages.</summary>
    public static IReadOnlyList<QuestRow> Quests(MapData data, bool main, string? filter, string lang, QuestProgress? progress = null,
        QuestFilter show = QuestFilter.All, bool hideDone = false)
    {
        var folded = TextFold.Fold(filter?.Trim());
        bool Matches(MapQuest quest) => string.IsNullOrEmpty(folded) || quest.Givers.Append(quest.Names)
            .Any(text => new[] { "en", "tr" }.Any(l => text.TryGet(l, out var value) && TextFold.Fold(value).Contains(folded, StringComparison.Ordinal)));
        bool Wanted(QuestStatus status) => status == QuestStatus.Unknown || (!hideDone || status != QuestStatus.Done || show == QuestFilter.Done) && show switch
        {
            QuestFilter.Active => status == QuestStatus.Active,
            QuestFilter.NotTaken => status == QuestStatus.NotTaken,
            QuestFilter.Done => status == QuestStatus.Done,
            _ => true,
        };

        return data.Quests.Where(q => q.IsMain == main && Matches(q) && Wanted(Status(q.Id, progress))).Select(q =>
        {
            var point = q.Points.Select(data.TryGetPoint).FirstOrDefault(p => p != null);
            var region = point?.RegionId == null ? null : data.TryGetRegion(point.RegionId)?.Names.Get(lang);
            return new QuestRow(q, q.Names.Get(lang), Giver(q, lang), region, point, Status(q.Id, progress), Step(q, progress));
        }).ToList();
    }

    /// <summary>The markers of a quest, one per place (markers within 5 m count as one; the giver is listed first).</summary>
    public static IReadOnlyList<MapMarker> QuestPlaces(MapQuest quest, MapData data)
    {
        var places = new List<MapMarker>();
        foreach (var point in quest.Points.Select(data.TryGetPoint).OfType<MapMarker>())
            if (!places.Any(p => p.Map == point.Map && Math.Abs(p.X - point.X) < 500 && Math.Abs(p.Y - point.Y) < 500)) places.Add(point);
        return places;
    }

    /// <summary>Who gives a quest ("Zoe"; two characters with a comma), or null when the data names none.</summary>
    public static string? Giver(MapQuest quest, string lang) =>
        quest.Givers.Count == 0 ? null : string.Join(", ", quest.Givers.Select(g => g.Get(lang)).Distinct(StringComparer.CurrentCulture));

    /// <summary>The quest givers by what is left to do with them: 1 every quest done, 2 a quest not taken yet; others are left out.</summary>
    public static IReadOnlyDictionary<string, int> GiverStates(IEnumerable<MapMarker> points, QuestProgress? progress)
    {
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        if (progress == null) return states;
        foreach (var point in points.Where(p => p.Category == "quest_giver" && p.Quests is { Count: > 0 }))
        {
            var status = point.Quests!.Select(q => Status(q, progress)).ToList();
            if (status.All(s => s == QuestStatus.Done)) states[point.Id] = 1;
            else if (status.Contains(QuestStatus.NotTaken)) states[point.Id] = 2;
        }

        return states;
    }

    // ---- predators (T149) ---------------------------------------------------------------------------------

    /// <summary>The areas of a map where the given predators may show up: the wild areas of their base species.</summary>
    public static IReadOnlyList<PredatorArea> PredatorAreas(IEnumerable<MapPredator> predators, IReadOnlyList<PalSpecies> pals, string mapId)
    {
        var areas = new List<PredatorArea>();
        foreach (var predator in predators)
        {
            var obtain = pals.FirstOrDefault(p => p.Id == predator.PalId)?.Obtain;
            foreach (var spawn in obtain?.Wild ?? [])
                areas.AddRange(spawn.Points.Where(p => p.Map == mapId).Select(p => new PredatorArea(predator, p, spawn.LevelMin, spawn.LevelMax)));
        }

        return areas;
    }

    // ---- bases (T151) -------------------------------------------------------------------------------------

    public static MapCategory BaseCategory => new(Base, "Locations", new LocalizedText(new Dictionary<string, string>(), Base), "icons/map/base.png", true);

    /// <summary>
    /// The markers of the player's bases, numbered in the order given (by base id). A base without a name of its own is called
    /// <paramref name="numbered"/> with its number.
    /// </summary>
    public static IReadOnlyList<MapMarker> BaseMarkers(IReadOnlyList<BaseSite> sites, Func<int, string> numbered, string lang) => sites
        .Select((site, index) =>
        {
            var map = MapProjection.Main.Contains(site.X, site.Y) ? Main : Tree;
            var coordinates = MapProjection.ForMap(map).ToMapCoordinates(site.X, site.Y);
            var name = site.Name ?? numbered(index + 1);
            return new MapMarker(BaseId(site.Id), Base, map, site.X, site.Y,
                coordinates == null ? null : (int)Math.Round(coordinates.Value.MapX), coordinates == null ? null : (int)Math.Round(coordinates.Value.MapY),
                new LocalizedText(new Dictionary<string, string> { [lang] = name }, name), null, Base, site.Id, null, Range: site.Range);
        }).ToList();

    public static string BaseId(string siteId) => Base + ":" + CaptureRecord.ObjectKey(siteId);

    /// <summary>
    /// The Pals that work at a base, in the game's slot order: from the sources of the world, each Pal once (the newest read).
    /// </summary>
    public static IReadOnlyList<OwnedPal> Workers(IEnumerable<OwnedPal> pals, string siteId, IReadOnlyCollection<string> sourceIds)
    {
        var key = CaptureRecord.ObjectKey(siteId);
        return pals.Where(p => p.Location.Kind == LocationKind.Base && p.Location.BaseId != null && CaptureRecord.ObjectKey(p.Location.BaseId) == key &&
                               sourceIds.Contains(p.SourceId))
            .GroupBy(p => p.InstanceId == null ? p.Uid : CaptureRecord.ObjectKey(p.InstanceId), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(p => p.SeenAtUtc).First())
            .OrderBy(p => p.Location.SlotIndex ?? int.MaxValue).ThenBy(p => p.Uid, StringComparer.Ordinal)
            .ToList();
    }
}
