namespace Paldium.Core.Owned;

/// <summary>
/// What the player's record says about the world (T144, T148–T150): the places and objects taken or opened, the journals read, the tower bosses
/// defeated, the quests and the defeated predators. The save file of the player and the mod give it; the mod's is the player on this computer.
/// </summary>
public sealed record WorldProgress
{
    /// <summary>Effigies and ancient towers taken, fast travel points and watchtowers opened: level object ids (<see cref="CaptureRecord.ObjectKey"/>).</summary>
    public IReadOnlySet<string> Objects { get; init; } = Set([]);

    /// <summary>Journals read, by their row name.</summary>
    public IReadOnlySet<string> Notes { get; init; } = Set([]);

    /// <summary>Tower bosses defeated, by the record's key (<c>BOSS_BATTLE_NAME_GrassBoss</c>).</summary>
    public IReadOnlySet<string> Towers { get; init; } = Set([]);

    /// <summary>Null when the source says nothing about quests (an older mod).</summary>
    public QuestProgress? Quests { get; init; }

    public int? PredatorDefeats { get; init; }

    public bool IsEmpty => Objects.Count == 0 && Notes.Count == 0 && Towers.Count == 0 && Quests == null && PredatorDefeats == null;

    public static IReadOnlySet<string> Set(IEnumerable<string> values) => values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One record from several of the same player, the newest first: what was once taken stays taken; the active quests are the newest source's,
    /// without those another source knows as done.
    /// </summary>
    public static WorldProgress? Merge(IReadOnlyList<WorldProgress> newestFirst)
    {
        if (newestFirst.Count == 0) return null;
        QuestProgress? quests = null;
        if (newestFirst.Select(p => p.Quests).OfType<QuestProgress>().ToList() is { Count: > 0 } known)
        {
            var done = Set(known.SelectMany(q => q.Done));
            quests = new QuestProgress(
                known[0].Active.Where(q => !done.Contains(q.Key)).ToDictionary(q => q.Key, q => q.Value, StringComparer.OrdinalIgnoreCase), done);
        }

        return new WorldProgress
        {
            Objects = Set(newestFirst.SelectMany(p => p.Objects)),
            Notes = Set(newestFirst.SelectMany(p => p.Notes)),
            Towers = Set(newestFirst.SelectMany(p => p.Towers)),
            Quests = quests,
            PredatorDefeats = newestFirst.Max(p => p.PredatorDefeats),
        };
    }
}

/// <summary>The quests a player is on (quest id to the step, counted from 0) and the ones finished.</summary>
public sealed record QuestProgress(IReadOnlyDictionary<string, int> Active, IReadOnlySet<string> Done);

/// <summary>
/// A base of the player (T151): where it is, and what a source knows beyond that. <see cref="Range"/> and <see cref="Capacity"/> come from the mod;
/// <see cref="Members"/> are the players of the owning guild (a save file), empty when unknown. Base names and places stay on this computer.
/// </summary>
public sealed record BaseSite(string Id, double X, double Y, double? Range = null, string? Name = null, int? Capacity = null)
{
    public IReadOnlyList<string> Members { get; init; } = [];
}
