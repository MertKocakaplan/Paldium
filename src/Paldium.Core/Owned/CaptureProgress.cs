namespace Paldium.Core.Owned;

/// <summary>
/// What a player's record says about catching Pals (T121): per species, the bonus catches counted so far (0 to 5; the 5th gives a Mimog statue)
/// and the total number of catches. Species ids are the ids of the game data. The same record also carries the player's effigies (T133).
/// </summary>
public sealed record CaptureRecord(IReadOnlyDictionary<string, int> Bonus, IReadOnlyDictionary<string, int> Count)
{
    public const int MaxBonus = 5;

    /// <summary>The effigies of the player by <c>EPalRelicType</c> name (T133); null when the source has none. A missing type means none known.</summary>
    public IReadOnlyDictionary<string, RelicCount>? Relics { get; init; }

    /// <summary>What the player has taken, opened and done in the world (T144); null when the source says nothing about it.</summary>
    public WorldProgress? Progress { get; init; }

    /// <summary>One spelling of an object id: a level object instance id without dashes and in capitals.</summary>
    public static string ObjectKey(string id) => id.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();

    public static CaptureRecord Empty { get; } = new(new Dictionary<string, int>(), new Dictionary<string, int>());

    public bool IsEmpty => Bonus.Count == 0 && Count.Count == 0 && (Relics == null || Relics.Count == 0) && (Progress == null || Progress.IsEmpty);

    /// <summary>The bonus catches of a species, 0 to 5; 0 when the species is not in the record.</summary>
    public int BonusOf(string speciesId) => Bonus.TryGetValue(speciesId, out var n) ? Math.Clamp(n, 0, MaxBonus) : 0;

    /// <summary>All catches of a species (can be more than 5); 0 when the species is not in the record.</summary>
    public int CountOf(string speciesId) => Count.TryGetValue(speciesId, out var n) ? Math.Max(0, n) : 0;
}

/// <summary>
/// The effigies of one type (T133): <see cref="Have"/> is what the player holds unspent, <see cref="Collected"/> what was ever collected from the world.
/// Either can be unknown (null): a client gets the collected ones from the mod only when the game synchronises them.
/// </summary>
public sealed record RelicCount(int? Have, int? Collected);

/// <summary>The capture record of one player and when it was read. <paramref name="IsLocal"/>: read by the mod, so the player of this computer.</summary>
public sealed record PlayerCapture(string PlayerId, CaptureRecord Record, DateTimeOffset ReadAtUtc, bool IsLocal = false);

/// <summary>Why the capture column has nothing to show (the table shows "-" and a hint).</summary>
public enum CaptureAvailability
{
    /// <summary>A record is known for the player.</summary>
    Available,

    /// <summary>No source gave a capture record.</summary>
    NoData,

    /// <summary>Records exist for several players and the user has not said which one they are.</summary>
    NoPlayer,
}

/// <summary>
/// What the sources know about the players (save files of a world, the mod's file): the capture records and the bases, kept in memory and read
/// again whenever a source is read.
/// Raises <see cref="Changed"/> from any thread.
/// </summary>
public sealed class CaptureProgressStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<PlayerCapture>> _bySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<BaseSite>> _bases = new(StringComparer.Ordinal);
    private long _version;

    public event EventHandler? Changed;

    /// <summary>Raised-change counter; the Paldex uses it to know when to work out its list again.</summary>
    public long Version { get { lock (_gate) return _version; } }

    /// <summary>Replaces what a source gave (an empty list means the source has no record).</summary>
    public void Publish(string sourceId, IReadOnlyList<PlayerCapture> players)
    {
        lock (_gate)
        {
            if (players.Count == 0) _bySource.Remove(sourceId);
            else _bySource[sourceId] = players;
            _version++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the bases a source gave.</summary>
    public void PublishBases(string sourceId, IReadOnlyList<BaseSite> bases)
    {
        lock (_gate)
        {
            if (bases.Count == 0 && !_bases.ContainsKey(sourceId)) return;
            if (bases.Count == 0) _bases.Remove(sourceId);
            else _bases[sourceId] = bases;
            _version++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string sourceId)
    {
        bool had;
        lock (_gate)
        {
            had = _bySource.Remove(sourceId) | _bases.Remove(sourceId);
            if (had) _version++;
        }

        if (had) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Player ids are compared without dashes and letter case (a save file and the game may write them differently).</summary>
    public static string NormalizeId(string? id) => (id ?? "").Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>
    /// The world progress of the player according to the given sources (T144): the named player, else the player the mod reads, else the only
    /// player there is. Null when those sources say nothing about that player.
    /// </summary>
    public WorldProgress? Progress(IReadOnlyCollection<string> sourceIds, string? myPlayerId)
    {
        var all = Of(sourceIds);
        var wanted = Me(all, myPlayerId);
        return wanted == null ? null : WorldProgress.Merge(all.Where(p => NormalizeId(p.PlayerId) == wanted && p.Record.Progress != null)
            .OrderByDescending(p => p.ReadAtUtc).Select(p => p.Record.Progress!).ToList());
    }

    /// <summary>
    /// The player's bases according to the given sources (T151), one per base: a base of a guild the player is not in is left out. The mod's
    /// place and range win over the save's.
    /// </summary>
    public IReadOnlyList<BaseSite> Bases(IReadOnlyCollection<string> sourceIds, string? myPlayerId)
    {
        var me = Me(Of(sourceIds), myPlayerId);
        List<BaseSite> sites;
        lock (_gate) sites = _bases.Where(s => sourceIds.Contains(s.Key)).SelectMany(s => s.Value).ToList();
        return sites.GroupBy(s => CaptureRecord.ObjectKey(s.Id), StringComparer.Ordinal)
            .Where(g => me == null || g.All(s => s.Members.Count == 0) || g.Any(s => s.Members.Any(m => NormalizeId(m) == me)))
            .Select(g =>
            {
                var best = g.OrderByDescending(s => s.Range.HasValue).First();
                return best with
                {
                    Id = g.Key,
                    Name = g.Select(s => s.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    Capacity = g.Select(s => s.Capacity).FirstOrDefault(c => c != null),
                };
            })
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    private List<PlayerCapture> Of(IReadOnlyCollection<string> sourceIds)
    {
        lock (_gate) return _bySource.Where(s => sourceIds.Contains(s.Key)).SelectMany(s => s.Value).ToList();
    }

    /// <summary>The normalized id of "me" among the records, or null when it cannot be told.</summary>
    private static string? Me(IReadOnlyList<PlayerCapture> all, string? myPlayerId)
    {
        var wanted = NormalizeId(myPlayerId);
        if (wanted.Length > 0) return wanted;
        if (all.Where(p => p.IsLocal).OrderByDescending(p => p.ReadAtUtc).FirstOrDefault() is { } local) return NormalizeId(local.PlayerId);
        var players = all.Select(p => NormalizeId(p.PlayerId)).Distinct(StringComparer.Ordinal).ToList();
        return players.Count == 1 ? players[0] : null;
    }

    /// <summary>
    /// The record of the player. With <paramref name="myPlayerId"/> the record of that player (the newest when several sources have one); without it
    /// the only player there is, or <see cref="CaptureAvailability.NoPlayer"/> when there are several.
    /// </summary>
    public (CaptureAvailability State, CaptureRecord? Record) Resolve(string? myPlayerId)
    {
        List<PlayerCapture> all;
        lock (_gate) all = _bySource.Values.SelectMany(x => x).ToList();
        if (all.Count == 0) return (CaptureAvailability.NoData, null);

        var wanted = NormalizeId(myPlayerId);
        if (wanted.Length == 0)
        {
            var players = all.Select(p => NormalizeId(p.PlayerId)).Distinct(StringComparer.Ordinal).ToList();
            if (players.Count != 1) return (CaptureAvailability.NoPlayer, null);
            wanted = players[0];
        }

        var matching = all.Where(p => NormalizeId(p.PlayerId) == wanted).OrderByDescending(p => p.ReadAtUtc).ToList();
        if (matching.Count == 0) return (CaptureAvailability.NoData, null);
        if (matching.Count == 1) return (CaptureAvailability.Available, matching[0].Record);

        var merged = matching[0].Record;
        var bonus = merged.Bonus;
        var count = merged.Count;
        var relics = merged.Relics == null ? null : new Dictionary<string, RelicCount>(merged.Relics, StringComparer.OrdinalIgnoreCase);

        foreach (var older in matching.Skip(1).Select(m => m.Record))
        {
            if (bonus.Count == 0 && older.Bonus.Count > 0) bonus = older.Bonus;
            if (count.Count == 0 && older.Count.Count > 0) count = older.Count;
            if (older.Relics == null) continue;
            if (relics == null)
            {
                relics = new Dictionary<string, RelicCount>(older.Relics, StringComparer.OrdinalIgnoreCase);
                continue;
            }

            foreach (var (type, prev) in older.Relics)
            {
                if (!relics.TryGetValue(type, out var cur))
                {
                    relics[type] = prev;
                    continue;
                }

                // Only fill Have from an older snapshot when Collected matches or was missing so unspent counts never mix across effigy pickups.
                var have = cur.Have ?? (cur.Collected == null || cur.Collected == prev.Collected ? prev.Have : null);
                var collected = cur.Collected ?? prev.Collected;
                if (have != cur.Have || collected != cur.Collected)
                    relics[type] = new RelicCount(have, collected);
            }
        }

        var progress = WorldProgress.Merge(matching.Select(m => m.Record.Progress).OfType<WorldProgress>().ToList());
        return (CaptureAvailability.Available, new CaptureRecord(bonus, count) { Relics = relics, Progress = progress });
    }
}
