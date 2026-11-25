using System.Text.Json;

using Paldium.Import.Migration;

namespace Paldium.Import.Bridge;

/// <summary>What <see cref="BridgePageStore.Merge"/> returns: the Pals of every stored page of the current world and the coverage of what was read.</summary>
public sealed record BridgeMergeResult(IReadOnlyList<BridgePal> Pals, IReadOnlyList<BridgeCoverage> Coverage, IReadOnlyList<BridgePlayer>? Players = null);

/// <summary>
/// Keeps the pages the mod has read across game sessions (ARCHITECTURE 6.5 "Oturumlar arası koruma"). The mod's own cache is lost with the game, so
/// the first export of a new session holds only the pages seen so far; this store merges each new file into the pages of earlier sessions.
/// One file per source: <c>cache\bridge-pages\&lt;sourceId&gt;.json</c>. Pages are keyed by (world, kind, box, page).
/// </summary>
public sealed class BridgePageStore
{
    public sealed record StoredPage(string World, string Kind, string Box, int Page, DateTimeOffset ReadAtUtc, IReadOnlyList<BridgePal> Pals);

    public sealed record StoredBox(string World, string Kind, string Box, string? OwnerUid, string? OwnerName, string? OwnerSource, int? PageCount)
    {
        // A base's place, range and worker slots (T151), kept for the bases a later session has not loaded.
        public double? X { get; init; }
        public double? Y { get; init; }
        public double? Range { get; init; }
        public int? Capacity { get; init; }
    }

    private sealed class State
    {
        public int Version { get; set; } = 1;
        public List<StoredPage> Pages { get; set; } = [];
        public List<StoredBox> Boxes { get; set; } = [];
        public List<BridgePlayer> Players { get; set; } = [];
    }

    private const string Dps = "DimensionalStorage";
    public const string UserOwnerSource = "user";

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private readonly string _folder;
    private readonly Action<string, Exception?>? _log;
    private readonly object _gate = new();

    public BridgePageStore(string? folder = null, Action<string, Exception?>? log = null)
    {
        _folder = folder ?? Path.Combine(AppFolders.Local, "cache", "bridge-pages");
        _log = log;
    }

    /// <summary>The world a file belongs to: the mod's <c>worldId</c>, else the player uid, else "unknown".</summary>
    public static string WorldOf(BridgeFile file) =>
        !string.IsNullOrEmpty(file.WorldId) ? file.WorldId : !string.IsNullOrEmpty(file.PlayerUid) ? file.PlayerUid : "unknown";

    // The box a page belongs to. The Pal Box and the party have one box each; storages are told apart by their box id / owner, bases by their id.
    private static string BoxKey(string kind, string? boxId, string? boxOwnerUid, string? baseId) => kind switch
    {
        "Palbox" or "Party" => "",
        "Base" => baseId ?? boxId ?? "",
        _ => !string.IsNullOrEmpty(boxId) ? boxId : ModBridgeReader.NormalizeOwner(boxOwnerUid) ?? "",
    };

    private string PathOf(string sourceId) => Path.Combine(_folder, string.Concat(sourceId.Where(c => !Path.GetInvalidFileNameChars().Contains(c))) + ".json");

    private State Load(string sourceId)
    {
        var path = PathOf(sourceId);
        try
        {
            if (!File.Exists(path)) return new State();
            return JsonSerializer.Deserialize<State>(BridgeFileIo.ReadAllText(path), Options) ?? new State();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log?.Invoke($"The page store of source {sourceId} is broken; starting with an empty one.", ex);
            return new State();
        }
    }

    private void Save(string sourceId, State state)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var path = PathOf(sourceId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"The page store of source {sourceId} could not be saved.", ex);
        }
    }

    /// <summary>
    /// Merges a new bridge file: every page the file read (<c>coverage[].pagesRead</c>, empty ones too, or any page that holds Pals) replaces the stored page;
    /// the party is replaced as a whole; pages the file did not read stay. Returns the Pals of all stored pages of the file's world, a Pal that is found in
    /// several pages kept once (the newest read wins).
    /// </summary>
    public BridgeMergeResult Merge(string sourceId, BridgeFile file)
    {
        lock (_gate)
        {
            var state = Load(sourceId);
            var world = WorldOf(file);
            var now = file.ExportedAtUtc ?? DateTimeOffset.UtcNow;

            var groups = file.Pals
                .GroupBy(p => (Kind: p.Location?.Kind ?? "Unknown", Box: BoxKey(p.Location?.Kind ?? "Unknown", p.Location?.BoxId, p.Location?.BoxOwnerUid, p.Location?.BaseId), Page: p.Location?.Page ?? 0))
                .ToDictionary(g => g.Key, g => g.ToList());

            var read = new Dictionary<(string Kind, string Box, int Page), DateTimeOffset>();
            foreach (var coverage in file.Coverage)
            {
                var box = BoxKey(coverage.Kind, coverage.BoxId, coverage.BoxOwnerUid, coverage.BoxId);
                var readAt = coverage.LastReadUtc ?? now;
                foreach (var page in coverage.PagesRead) read[(coverage.Kind, box, page)] = readAt;

                var previous = state.Boxes.FirstOrDefault(b => b.World == world && b.Kind == coverage.Kind && b.Box == box);
                state.Boxes.RemoveAll(b => b.World == world && b.Kind == coverage.Kind && b.Box == box);
                var uid = ModBridgeReader.NormalizeOwner(coverage.BoxOwnerUid);
                // A file that does not know the owner keeps what is known (the game's answer of an earlier session, or the user's choice).
                var known = uid != null || !string.IsNullOrEmpty(coverage.OwnerName);
                var ownerName = !string.IsNullOrEmpty(coverage.OwnerName) ? coverage.OwnerName : previous?.OwnerName;
                var ownerSource = !string.IsNullOrEmpty(coverage.OwnerSource) ? coverage.OwnerSource : previous?.OwnerSource;
                var stored = known || previous == null
                    ? new StoredBox(world, coverage.Kind, box, uid ?? previous?.OwnerUid, ownerName, ownerSource, coverage.PageCount ?? previous?.PageCount)
                    : previous with { PageCount = coverage.PageCount ?? previous.PageCount };
                state.Boxes.Add(stored with
                {
                    X = coverage.X ?? previous?.X, Y = coverage.Y ?? previous?.Y, Range = coverage.Range ?? previous?.Range,
                    Capacity = coverage.Capacity ?? previous?.Capacity,
                });
            }

            // Pages that hold Pals count as read even when an older mod wrote no coverage.
            foreach (var key in groups.Keys) read.TryAdd(key, now);

            // The party is rewritten as a whole.
            if (file.Pals.Any(p => p.Location?.Kind == "Party") || file.Coverage.Any(c => c.Kind == "Party"))
                state.Pages.RemoveAll(p => p.World == world && p.Kind == "Party");

            foreach (var (key, readAt) in read)
            {
                state.Pages.RemoveAll(p => p.World == world && p.Kind == key.Kind && p.Box == key.Box && p.Page == key.Page);
                state.Pages.Add(new StoredPage(world, key.Kind, key.Box, key.Page, readAt, groups.TryGetValue(key, out var pals) ? pals : []));
            }

            foreach (var player in file.Players)
            {
                if (string.IsNullOrEmpty(player.Uid)) continue;
                state.Players.RemoveAll(p => string.Equals(p.Uid, player.Uid, StringComparison.OrdinalIgnoreCase));
                state.Players.Add(player);
            }

            Consolidate(state, world);
            Save(sourceId, state);
            return Build(state, world);
        }
    }

    /// <summary>The players known so far (the mod's <c>players</c> array of earlier files), for the owner choice of the sources panel.</summary>
    public IReadOnlyList<BridgePlayer> Players(string sourceId)
    {
        lock (_gate) return Load(sourceId).Players.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The user says whose storage a box is (T60). The choice is stored with the box (source <c>user</c>); when the game later tells the owner, the game's
    /// answer wins. A storage of the same player is the same storage, so it is merged into that player's box.
    /// </summary>
    public BridgeMergeResult SetBoxOwner(string sourceId, string world, string box, string? ownerUid, string? ownerName)
    {
        lock (_gate)
        {
            var state = Load(sourceId);
            var existing = state.Boxes.FirstOrDefault(b => b.World == world && b.Kind == Dps && b.Box == box);
            state.Boxes.RemoveAll(b => b.World == world && b.Kind == Dps && b.Box == box);
            state.Boxes.Add(new StoredBox(world, Dps, box, ModBridgeReader.NormalizeOwner(ownerUid), ownerName, UserOwnerSource, existing?.PageCount));
            Consolidate(state, world);
            Save(sourceId, state);
            return Build(state, world);
        }
    }

    // ---- storage identity from content (T60, ARCHITECTURE 6.2) --------------------------------------------------------------------------------

    private static bool IsUnknownKey(string box) => box.StartsWith("unknown", StringComparison.Ordinal);

    // Who owns a storage box, as far as anything says: the uid, else a known (non unknown-) key, else a chosen or read name. Null: nobody knows.
    private static string? IdentityOf(string box, StoredBox? record)
    {
        if (record?.OwnerUid is { } uid) return "uid:" + uid.ToUpperInvariant();
        if (!IsUnknownKey(box) && box.Length > 0) return box.StartsWith("name:", StringComparison.Ordinal) ? "name:" + box[5..] : "uid:" + box.ToUpperInvariant();
        if (!string.IsNullOrEmpty(record?.OwnerName)) return "name:" + record.OwnerName;
        return null;
    }

    // 2: the game told the owner (or the key is the owner), 1: the user chose, 0: unknown.
    private static int Rank(string box, StoredBox? record) =>
        IdentityOf(box, record) == null ? 0 : record?.OwnerSource == UserOwnerSource && IsUnknownKey(box) ? 1 : 2;

    // The Pals of a page as instance ids (a Pal without an id cannot be matched).
    private static HashSet<string> IdsOf(StoredPage page) =>
        page.Pals.Select(p => p.InstanceId).Where(i => !string.IsNullOrEmpty(i)).Select(i => i!.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Two pages with the same number are "the same page" when the Pals that are in both (same instance id, same page number) are at least half of the
    /// Pals of each page (symmetric, at least 50 %). One shared Pal is not enough: Pals are moved between the storages of the players (ARCHITECTURE 6.2).
    /// </summary>
    public static bool SamePage(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return false;
        var matched = a.Count(b.Contains);
        return matched * 2 >= a.Count && matched * 2 >= b.Count;
    }

    // Two storage boxes are the same storage when most of the pages that both have filled (same page number) are the same page.
    private static bool SameStorage(IReadOnlyDictionary<int, HashSet<string>> x, IReadOnlyDictionary<int, HashSet<string>> y)
    {
        var comparable = 0;
        var same = 0;
        foreach (var (page, idsX) in x)
        {
            if (idsX.Count == 0 || !y.TryGetValue(page, out var idsY) || idsY.Count == 0) continue;
            comparable++;
            if (SamePage(idsX, idsY)) same++;
        }

        return comparable > 0 && same * 2 > comparable;
    }

    /// <summary>
    /// Tidies the Dimensional Pal Storage boxes of a world (T60, ARCHITECTURE 6.2 "same storage" rule):
    /// 1. boxes whose same-numbered pages are mostly "the same page" (or that have the same known owner) are one storage; never two different known owners;
    /// 2. a box takes in another box when at least 90 % of the other's Pals were read last in it (a sorted storage read under a new key);
    /// 3. a box none of whose Pals was read last in it is deleted, its owner known or not.
    /// A merged box keeps the known owner (the game's answer before the user's choice); the newest read of a page wins.
    /// </summary>
    private static void Consolidate(State state, string world)
    {
        StoredBox? Record(string key) => state.Boxes.FirstOrDefault(b => b.World == world && b.Kind == Dps && b.Box == key);
        List<string> Keys() => state.Pages.Where(p => p.World == world && p.Kind == Dps).Select(p => p.Box)
            .Concat(state.Boxes.Where(b => b.World == world && b.Kind == Dps).Select(b => b.Box)).Distinct(StringComparer.Ordinal).ToList();
        Dictionary<int, HashSet<string>> PagesOf(string key) => state.Pages.Where(p => p.World == world && p.Kind == Dps && p.Box == key)
            .GroupBy(p => p.Page).ToDictionary(g => g.Key, g => g.SelectMany(IdsOf).ToHashSet(StringComparer.Ordinal));
        bool Conflict(string x, string y)
        {
            var idX = IdentityOf(x, Record(x));
            var idY = IdentityOf(y, Record(y));
            return idX != null && idY != null && idX != idY;
        }

        // Moves every page of `other` into `target` (the newest read of a page wins) and removes `other`.
        void MergeInto(string target, string other)
        {
            foreach (var page in state.Pages.Where(p => p.World == world && p.Kind == Dps && p.Box == other).ToList())
            {
                state.Pages.Remove(page);
                var existing = state.Pages.FirstOrDefault(p => p.World == world && p.Kind == Dps && p.Box == target && p.Page == page.Page);
                if (existing != null && existing.ReadAtUtc > page.ReadAtUtc) continue;
                if (existing != null) state.Pages.Remove(existing);
                state.Pages.Add(page with { Box = target });
            }

            var record = Record(other);
            if (record == null) return;
            state.Boxes.Remove(record);
            var targetRecord = Record(target);
            if (targetRecord == null) state.Boxes.Add(record with { Box = target });
            else if (targetRecord.PageCount == null && record.PageCount != null) state.Boxes[state.Boxes.IndexOf(targetRecord)] = targetRecord with { PageCount = record.PageCount };
        }

        // The box that stays when two boxes become one: the known owner, then the one with more Pals.
        string Winner(string x, string y)
        {
            var rx = Rank(x, Record(x));
            var ry = Rank(y, Record(y));
            if (rx != ry) return rx > ry ? x : y;
            var cx = PagesOf(x).Values.Sum(v => v.Count);
            var cy = PagesOf(y).Values.Sum(v => v.Count);
            if (cx != cy) return cx > cy ? x : y;
            return string.CompareOrdinal(x, y) <= 0 ? x : y;
        }

        // 1. Same storage by page content (or by the same known owner).
        for (var changed = true; changed;)
        {
            changed = false;
            var keys = Keys();
            for (var i = 0; i < keys.Count && !changed; i++)
                for (var j = i + 1; j < keys.Count && !changed; j++)
                {
                    if (Conflict(keys[i], keys[j])) continue;
                    var idA = IdentityOf(keys[i], Record(keys[i]));
                    var sameOwner = idA != null && idA == IdentityOf(keys[j], Record(keys[j]));
                    if (!sameOwner && !SameStorage(PagesOf(keys[i]), PagesOf(keys[j]))) continue;
                    var winner = Winner(keys[i], keys[j]);
                    MergeInto(winner, winner == keys[i] ? keys[j] : keys[i]);
                    changed = true;
                }
        }

        // The last read of every Pal (any kind of box): the page that was read last holds it, like Build decides.
        Dictionary<string, (string Kind, string Box)> LastRead()
        {
            var last = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var page in state.Pages.Where(p => p.World == world).OrderBy(p => p.ReadAtUtc))
                foreach (var id in IdsOf(page)) last[id] = (page.Kind, page.Box);
            return last;
        }

        // 2. Absorption: a storage whose Pals were nearly all read last in one other storage.
        for (var changed = true; changed;)
        {
            changed = false;
            var last = LastRead();
            foreach (var key in Keys())
            {
                var ids = PagesOf(key).Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);
                if (ids.Count == 0) continue;
                var elsewhere = ids.Where(id => last.TryGetValue(id, out var where) && where.Kind == Dps && where.Box != key)
                    .GroupBy(id => last[id].Box).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (elsewhere == null || elsewhere.Count() * 10 < ids.Count * 9 || Conflict(key, elsewhere.Key)) continue;
                var winner = Winner(key, elsewhere.Key);
                MergeInto(winner, winner == key ? elsewhere.Key : key);
                changed = true;
                break;
            }
        }

        // 3. A storage or base none of whose Pals was read last in it is gone (emptied, sorted away, or dismantled).
        var finalLast = LastRead();
        var targets = Keys().Select(k => (Kind: Dps, Key: k))
            .Concat(state.Pages.Where(p => p.World == world && p.Kind == "Base").Select(p => p.Box)
                .Concat(state.Boxes.Where(b => b.World == world && b.Kind == "Base").Select(b => b.Box))
                .Distinct(StringComparer.Ordinal).Select(k => (Kind: "Base", Key: k)))
            .ToList();
        foreach (var (kind, key) in targets)
        {
            var pages = state.Pages.Where(p => p.World == world && p.Kind == kind && p.Box == key).ToList();
            var holdsSomething = pages.Any(page => page.Pals.Any(pal => string.IsNullOrEmpty(pal.InstanceId))
                || IdsOf(page).Any(id => finalLast.TryGetValue(id, out var where) && where.Kind == kind && where.Box == key));
            if (holdsSomething) continue;
            state.Pages.RemoveAll(p => p.World == world && p.Kind == kind && p.Box == key);
            state.Boxes.RemoveAll(b => b.World == world && b.Kind == kind && b.Box == key);
        }
    }

    /// <summary>Coverage of the stored pages of a world (what the sources panel shows), without merging a file.</summary>
    public IReadOnlyList<BridgeCoverage> Coverage(string sourceId, string world)
    {
        lock (_gate) return Build(Load(sourceId), world).Coverage;
    }

    /// <summary>Forgets every stored page of a source ("clear the page cache").</summary>
    public void Clear(string sourceId)
    {
        lock (_gate)
        {
            try
            {
                var path = PathOf(sourceId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"The page store of source {sourceId} could not be cleared.", ex);
            }
        }
    }

    private static BridgeMergeResult Build(State state, string world)
    {
        var pages = state.Pages.Where(p => p.World == world).ToList();

        // A Pal seen on several pages (it was moved): the page that was read last decides.
        var byInstance = new Dictionary<string, (BridgePal Pal, DateTimeOffset ReadAt)>(StringComparer.OrdinalIgnoreCase);
        var anonymous = new List<BridgePal>();
        foreach (var page in pages.OrderBy(p => p.ReadAtUtc))
            foreach (var pal in page.Pals)
            {
                // Every Pal is as old as the page it was read from, not as the file that was written last.
                var stamped = pal with { ReadAtUtc = page.ReadAtUtc };
                // A storage that was merged into another (T60): its Pals belong to the box they are stored in now.
                if (page.Kind == Dps && pal.Location != null)
                {
                    var record = state.Boxes.FirstOrDefault(b => b.World == world && b.Kind == Dps && b.Box == page.Box);
                    stamped = stamped with { Location = pal.Location with { BoxId = string.IsNullOrEmpty(page.Box) ? pal.Location.BoxId : page.Box, BoxOwnerUid = record?.OwnerUid } };
                }

                if (string.IsNullOrEmpty(pal.InstanceId)) anonymous.Add(stamped);
                else byInstance[pal.InstanceId] = (stamped, page.ReadAtUtc);
            }

        var pals = byInstance.Values.Select(v => v.Pal).Concat(anonymous).ToList();

        var coverage = pages.GroupBy(p => (p.Kind, p.Box)).Select(g =>
        {
            var box = state.Boxes.FirstOrDefault(b => b.World == world && b.Kind == g.Key.Kind && b.Box == g.Key.Box);
            return new BridgeCoverage
            {
                Kind = g.Key.Kind,
                BoxOwnerUid = box?.OwnerUid,
                BoxId = string.IsNullOrEmpty(g.Key.Box) ? null : g.Key.Box,
                OwnerName = box?.OwnerName,
                OwnerSource = box?.OwnerSource,
                X = box?.X, Y = box?.Y, Range = box?.Range, Capacity = box?.Capacity,
                PageCount = box?.PageCount,
                PalCount = g.SelectMany(p => p.Pals).Select(p => p.InstanceId ?? Guid.NewGuid().ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                PagesRead = g.Select(p => p.Page).Distinct().Order().ToList(),
                LastReadUtc = g.Max(p => p.ReadAtUtc),
                OldestReadUtc = g.Min(p => p.ReadAtUtc),
            };
        }).OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.BoxId, StringComparer.Ordinal).ToList();

        return new BridgeMergeResult(pals, coverage, state.Players);
    }
}
