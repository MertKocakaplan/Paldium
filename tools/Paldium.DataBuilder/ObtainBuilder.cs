using System.Text.Json;
using Paldium.Core.Data.Schema;

namespace Paldium.DataBuilder;

/// <summary>
/// Joins the extractor's obtain tables (spawner rows, placements, dungeons, cages, shops, fishing, raids) into the
/// per-species <c>obtain</c> data (ARCHITECTURE 4.3).
/// </summary>
public sealed class ObtainBuilder
{
    private const double MinimalSpawnWeight = 0;

    private sealed class Accumulator
    {
        public SortedSet<string> Methods { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, WildSpawnDto> Wild { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, BossSpawnDto> Bosses { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DungeonSpawnDto> Dungeons { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CageDto> Cages { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, MerchantDto> Merchants { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, FishingDto> Fishing { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RaidDto> Raids { get; } = new(StringComparer.Ordinal);
        public bool TowerBoss { get; set; }
    }

    // Order of methods in the bundle (ARCHITECTURE 4.3).
    private static readonly string[] MethodOrder =
        ["Wild", "AlphaBoss", "Imprisoned", "Dungeon", "DungeonBoss", "Cage", "Merchant", "Fishing", "Raid", "TowerBoss"];

    private static readonly string[] IdPrefixes = ["BOSS_", "RAID_"];

    private readonly Dictionary<string, string> _canonicalIds;
    private readonly Dictionary<string, Accumulator> _byPal = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, SortedDictionary<string, string>> _regionNames;

    /// <summary>Region keys without a name: key to number of wild records.</summary>
    public SortedDictionary<string, int> UnmappedRegions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int SkippedZeroWeightRows { get; private set; }
    public int SkippedPlaceholderSlots { get; private set; }
    public SortedDictionary<string, int> UnrecognizedPalIds { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> UnmatchedSpawners { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, SortedSet<string>> UnmatchedSpawnerPals { get; } = new(StringComparer.Ordinal);
    public int UnmatchedSpawnerRows { get; private set; }
    public int UnmatchedFieldBossRows { get; private set; }
    public int PointsOutsideMaps { get; private set; }
    public int WildRecords => _byPal.Values.Sum(a => a.Wild.Count);
    public int WildPoints => _byPal.Values.Sum(a => a.Wild.Values.Sum(w => w.Points.Count));
    public int BossRecords => _byPal.Values.Sum(a => a.Bosses.Count);
    public int DungeonRecords => _byPal.Values.Sum(a => a.Dungeons.Count);

    public ObtainBuilder(IEnumerable<string> palIds, IReadOnlyDictionary<string, SortedDictionary<string, string>>? regionNames = null)
    {
        _regionNames = regionNames ?? new Dictionary<string, SortedDictionary<string, string>>();
        _canonicalIds = palIds.ToDictionary(id => id, id => id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Species id of a raw game id, or null. "BOSS_X" / "RAID_X" map to X; anything else must match exactly.</summary>
    public string? Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (_canonicalIds.TryGetValue(raw, out var id)) return id;
        foreach (var prefix in IdPrefixes)
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                _canonicalIds.TryGetValue(raw[prefix.Length..], out id))
                return id;
        return null;
    }

    private Accumulator For(string id)
    {
        if (!_byPal.TryGetValue(id, out var a)) _byPal[id] = a = new Accumulator();
        return a;
    }

    private string? Recognize(string rawId)
    {
        var id = Normalize(rawId);
        if (id == null) UnrecognizedPalIds[rawId] = UnrecognizedPalIds.GetValueOrDefault(rawId) + 1;
        return id;
    }

    public void AddTowerBosses(IEnumerable<string> tribes)
    {
        foreach (var tribe in tribes)
        {
            var id = Normalize(tribe);
            if (id == null) continue;
            var a = For(id);
            a.TowerBoss = true;
            a.Methods.Add("TowerBoss");
        }
    }

    public void AddSpawns(JsonElement obtain, Func<string, SortedDictionary<string, string>> dungeonNames)
    {
        var placements = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var p in obtain.GetProperty("placements").EnumerateArray())
        {
            var spawner = p.GetProperty("spawner").GetString()!;
            if (!placements.TryGetValue(spawner, out var list)) placements[spawner] = list = [];
            list.Add(p);
        }

        var rows = obtain.GetProperty("wildSpawner").EnumerateArray().ToList();
        var dungeonSpawners = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var e in obtain.GetProperty("dungeonEnemies").EnumerateArray())
        {
            if (e.GetProperty("area").GetString()!.StartsWith("TestDebug", StringComparison.Ordinal)) continue;
            var spawner = e.GetProperty("spawner").GetString()!;
            if (!dungeonSpawners.TryGetValue(spawner, out var list)) dungeonSpawners[spawner] = list = [];
            list.Add(e);
        }

        foreach (var row in rows)
        {
            var spawner = row.GetProperty("spawner").GetString()!;
            var weight = row.GetProperty("weight").GetDouble();
            var slots = row.GetProperty("slots").EnumerateArray().ToList();

            if (weight <= MinimalSpawnWeight) { SkippedZeroWeightRows++; continue; }

            var hasPlacement = placements.TryGetValue(spawner, out var spawnerPlacements);
            var isDungeon = dungeonSpawners.ContainsKey(spawner);
            var usedField = false;

            foreach (var slot in slots)
            {
                var raw = slot.GetProperty("pal").GetString()!;
                if (raw == "RowName") { SkippedPlaceholderSlots++; continue; }
                var id = Recognize(raw);
                if (id == null) continue;
                var min = slot.GetProperty("levelMin").GetInt32();
                var max = slot.GetProperty("levelMax").GetInt32();

                // Field / FieldBoss / ImprisonmentBoss placements.
                if (hasPlacement)
                {
                    var field = spawnerPlacements!.Where(p => p.GetProperty("placement").GetString() == "Field").ToList();
                    var boss = spawnerPlacements!.Where(p => p.GetProperty("placement").GetString() is "FieldBoss" or "ImprisonmentBoss").ToList();
                    if (field.Count > 0) { AddWild(id, row, spawner, min, max, field); usedField = true; }
                    foreach (var p in boss) { AddBoss(id, spawner, min, p); usedField = true; }
                }

                if (isDungeon)
                    foreach (var d in dungeonSpawners[spawner])
                        AddDungeon(id, d, min, max, dungeonNames);
            }

            if (!usedField && !isDungeon && slots.Count > 0)
            {
                UnmatchedSpawnerRows++;
                UnmatchedSpawners.Add(spawner);
                if (row.GetProperty("type").GetString() == "FieldBoss") UnmatchedFieldBossRows++;
                if (!UnmatchedSpawnerPals.TryGetValue(spawner, out var pals)) UnmatchedSpawnerPals[spawner] = pals = new(StringComparer.Ordinal);
                foreach (var s in slots) pals.Add(s.GetProperty("pal").GetString()!);
            }
        }
    }

    private MapPointDto Point(JsonElement p)
    {
        double x = p.GetProperty("x").GetDouble(), y = p.GetProperty("y").GetDouble();
        var (map, mapX, mapY) = MapTransform.Locate(x, y);
        if (map == null) PointsOutsideMaps++;
        return new MapPointDto(x, y, p.GetProperty("radius").GetInt32(), map, mapX, mapY);
    }

    private void AddWild(string id, JsonElement row, string spawner, int min, int max, List<JsonElement> field)
    {
        var rowKey = row.GetProperty("row").GetString()!;
        var region = rowKey.Split('_')[0];
        var night = row.GetProperty("onlyTime").ValueKind == JsonValueKind.String &&
                    row.GetProperty("onlyTime").GetString() == "Night";
        var aura = row.GetProperty("worldTreeAura").GetBoolean();
        var a = For(id);
        a.Methods.Add("Wild");
        var key = $"{spawner}|{min}|{max}|{night}|{aura}";
        if (a.Wild.ContainsKey(key)) return;
        var points = field.Select(Point)
            .OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        SortedDictionary<string, string>? names = null;
        if (!_regionNames.TryGetValue(region, out names)) UnmappedRegions[region] = UnmappedRegions.GetValueOrDefault(region) + 1;
        a.Wild[key] = new WildSpawnDto(spawner, region, names, min, max, night, aura, points);
    }

    private void AddBoss(string id, string spawner, int level, JsonElement placement)
    {
        var kind = placement.GetProperty("placement").GetString() == "FieldBoss" ? "Alpha" : "Imprisoned";
        var a = For(id);
        a.Methods.Add(kind == "Alpha" ? "AlphaBoss" : "Imprisoned");
        var point = Point(placement);
        a.Bosses[$"{kind}|{spawner}|{level}|{point.X}|{point.Y}"] = new BossSpawnDto(kind, spawner, level, point);
    }

    private void AddDungeon(string id, JsonElement dungeon, int min, int max, Func<string, SortedDictionary<string, string>> dungeonNames)
    {
        var area = dungeon.GetProperty("area").GetString()!;
        var rawRank = dungeon.GetProperty("rank").GetString();
        var rank = rawRank is "Boss" or "MidBoss" ? rawRank : "Normal";
        var a = For(id);
        a.Methods.Add(rank == "Boss" ? "DungeonBoss" : "Dungeon");
        a.Dungeons[$"{area}|{rank}|{min}|{max}"] = new DungeonSpawnDto(area, dungeonNames(area), rank, min, max);
    }

    public void AddOther(JsonElement obtain)
    {
        foreach (var c in obtain.GetProperty("cages").EnumerateArray())
        {
            if (c.GetProperty("pal").GetString() is not { } raw || Recognize(raw) is not { } id) continue;
            var dto = new CageDto(c.GetProperty("field").GetString()!, c.GetProperty("levelMin").GetInt32(),
                c.GetProperty("levelMax").GetInt32(), c.GetProperty("weight").GetDouble());
            var a = For(id);
            a.Methods.Add("Cage");
            a.Cages[$"{dto.Field}|{dto.LevelMin}|{dto.LevelMax}|{dto.Weight}"] = dto;
        }

        foreach (var shop in obtain.GetProperty("shops").EnumerateArray())
        {
            var name = shop.GetProperty("shop").GetString()!;
            if (name.StartsWith("Test_", StringComparison.Ordinal)) continue;
            foreach (var pal in shop.GetProperty("pals").EnumerateArray())
            {
                if (Recognize(pal.GetString()!) is not { } id) continue;
                var dto = new MerchantDto(name, shop.GetProperty("levelMin").GetInt32(), shop.GetProperty("levelMax").GetInt32());
                var a = For(id);
                a.Methods.Add("Merchant");
                a.Merchants[$"{dto.Shop}|{dto.LevelMin}|{dto.LevelMax}"] = dto;
            }
        }

        foreach (var f in obtain.GetProperty("fishing").EnumerateArray())
        {
            if (f.GetProperty("pal").GetString() is not { } raw || Recognize(raw) is not { } id) continue;
            AddFishing(id, new FishingDto(f.GetProperty("lottery").GetString()!, f.GetProperty("group").GetString()!,
                f.GetProperty("levelMin").GetInt32(), f.GetProperty("levelMax").GetInt32(), f.GetProperty("weight").GetDouble()));
        }

        // Fishing spot Pals have no lottery name or weight in their table; kept as an unweighted "FishingSpot" entry.
        foreach (var f in obtain.GetProperty("fishingSpots").EnumerateArray())
        {
            if (f.GetProperty("pal").GetString() is not { } raw || Recognize(raw) is not { } id) continue;
            AddFishing(id, new FishingDto("FishingSpot", "FishingSpot", f.GetProperty("levelMin").GetInt32(),
                f.GetProperty("levelMax").GetInt32(), 1.0));
        }

        foreach (var r in obtain.GetProperty("raids").EnumerateArray())
        {
            if (r.GetProperty("pal").GetString() is not { } raw || Recognize(raw) is not { } id) continue;
            var dto = new RaidDto(r.GetProperty("summon").GetString()!, r.GetProperty("level").GetInt32());
            var a = For(id);
            a.Methods.Add("Raid");
            a.Raids[$"{dto.Summon}|{dto.Level}"] = dto;
        }
    }

    private void AddFishing(string id, FishingDto dto)
    {
        var a = For(id);
        a.Methods.Add("Fishing");
        a.Fishing[$"{dto.Lottery}|{dto.Group}|{dto.LevelMin}|{dto.LevelMax}|{dto.Weight}"] = dto;
    }

    /// <summary>The finished obtain data of a species. <paramref name="sameSpeciesOnly"/> comes from the breeding flags.</summary>
    public ObtainDto Build(string id, bool sameSpeciesOnly, bool recipeChild = false)
    {
        _byPal.TryGetValue(id, out var a);
        var methods = MethodOrder.Where(m => a?.Methods.Contains(m) == true).ToList();
        if (sameSpeciesOnly) methods.Add("SameSpeciesOnly");
        if (recipeChild) methods.Add("Recipe");
        if (a == null) return new ObtainDto(methods, [], [], [], [], [], [], [], false);

        return new ObtainDto(
            methods,
            a.Wild.Values.OrderBy(w => w.Spawner, StringComparer.Ordinal).ThenBy(w => w.LevelMin).ThenBy(w => w.LevelMax)
                .ThenBy(w => w.Night).ThenBy(w => w.WorldTreeAura).ToList(),
            a.Bosses.Values.OrderBy(b => b.Kind, StringComparer.Ordinal).ThenBy(b => b.Spawner, StringComparer.Ordinal)
                .ThenBy(b => b.Level).ThenBy(b => b.Point.X).ThenBy(b => b.Point.Y).ToList(),
            a.Dungeons.Values.OrderBy(d => d.Area, StringComparer.Ordinal).ThenBy(d => d.Rank, StringComparer.Ordinal)
                .ThenBy(d => d.LevelMin).ThenBy(d => d.LevelMax).ToList(),
            a.Cages.Values.OrderBy(c => c.Field, StringComparer.Ordinal).ThenBy(c => c.LevelMin).ThenBy(c => c.LevelMax).ToList(),
            a.Merchants.Values.OrderBy(m => m.Shop, StringComparer.Ordinal).ThenBy(m => m.LevelMin).ToList(),
            a.Fishing.Values.OrderBy(f => f.Lottery, StringComparer.Ordinal).ThenBy(f => f.Group, StringComparer.Ordinal)
                .ThenBy(f => f.LevelMin).ThenBy(f => f.LevelMax).ToList(),
            a.Raids.Values.OrderBy(r => r.Summon, StringComparer.Ordinal).ThenBy(r => r.Level).ToList(),
            a.TowerBoss);
    }
}
