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

    public static CaptureRecord Empty { get; } = new(new Dictionary<string, int>(), new Dictionary<string, int>());

    public bool IsEmpty => Bonus.Count == 0 && Count.Count == 0 && (Relics == null || Relics.Count == 0);

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

/// <summary>The capture record of one player and when it was read.</summary>
public sealed record PlayerCapture(string PlayerId, CaptureRecord Record, DateTimeOffset ReadAtUtc);

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
/// The capture records of the sources (save files of a world, the mod's file), kept in memory: they are read again whenever a source is read.
/// Raises <see cref="Changed"/> from any thread.
/// </summary>
public sealed class CaptureProgressStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<PlayerCapture>> _bySource = new(StringComparer.Ordinal);
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

    public void Remove(string sourceId)
    {
        bool had;
        lock (_gate)
        {
            had = _bySource.Remove(sourceId);
            if (had) _version++;
        }

        if (had) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Player ids are compared without dashes and letter case (a save file and the game may write them differently).</summary>
    public static string NormalizeId(string? id) => (id ?? "").Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

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

        return (CaptureAvailability.Available, new CaptureRecord(bonus, count) { Relics = relics });
    }
}
