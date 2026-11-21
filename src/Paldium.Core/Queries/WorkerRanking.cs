using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Queries;

public enum WorkerStage { Early, Mid, Late }

/// <summary>One species in a worker column.</summary>
/// <param name="WorkLevel">Work suitability level of the work type.</param>
/// <param name="EarliestLevel">Lowest level at which the species can be obtained other than by breeding; null when it can only be bred.</param>
/// <param name="EarliestMethod">The obtain method of that level (<c>Wild</c>, <c>AlphaBoss</c>, <c>Imprisoned</c>, <c>Raid</c>, <c>Dungeon</c>, <c>Cage</c>, <c>Merchant</c>, <c>Fishing</c>).</param>
/// <param name="OtherWork">How many other work types the species has.</param>
public sealed record WorkerEntry(PalSpecies Pal, int WorkLevel, int? EarliestLevel, string? EarliestMethod, int OtherWork);

/// <summary>The best workers of one work type per game stage (T54).</summary>
public sealed record WorkerColumns(string WorkType, IReadOnlyList<WorkerEntry> Early, IReadOnlyList<WorkerEntry> Mid, IReadOnlyList<WorkerEntry> Late)
{
    public IReadOnlyList<WorkerEntry> Of(WorkerStage stage) => stage switch { WorkerStage.Early => Early, WorkerStage.Mid => Mid, _ => Late };
}

/// <summary>
/// "Best workers per stage" (RESEARCH 12.4.1): for every work type the first three species of each stage. A species belongs to a stage when it can
/// be obtained at that level (<see cref="EarlyMaxLevel"/>, <see cref="MidMaxLevel"/>); the last stage holds everything, also species that can only
/// be bred. Order: work level (high first), earliest level (low first), number of other work types (many first), dex number.
/// </summary>
public static class WorkerRanking
{
    public const int EarlyMaxLevel = 15;
    public const int MidMaxLevel = 35;
    public const int PerColumn = 3;

    /// <summary>Lowest level (above 0) at which the species can be obtained without breeding, and by which method.</summary>
    public static (int? Level, string? Method) Earliest(PalSpecies pal)
    {
        // A species the game does not let the player catch is only available by breeding.
        if (pal.IsUncapturable) return (null, null);

        var candidates = new List<(int Level, string Method)>();
        void Add(string method, int level)
        {
            if (level > 0) candidates.Add((level, method));
        }

        if (pal.WildLevel is { Min: > 0 } wild) Add("Wild", wild.Min);
        if (pal.Obtain is { } obtain)
        {
            foreach (var spawn in obtain.Wild) Add("Wild", spawn.LevelMin);
            foreach (var boss in obtain.Bosses.Where(b => b.Kind == "Alpha")) Add("AlphaBoss", boss.Level);
            // Imprisoned bosses and raids are ways to obtain a species too (ARCHITECTURE 7.3); five species have no other way.
            foreach (var boss in obtain.Bosses.Where(b => b.Kind == "Imprisoned")) Add("Imprisoned", boss.Level);
            foreach (var raid in obtain.Raids) Add("Raid", raid.Level);
            foreach (var dungeon in obtain.Dungeons) Add("Dungeon", dungeon.LevelMin);
            foreach (var cage in obtain.Cages) Add("Cage", cage.LevelMin);
            // The game's test shops are not part of the game for players.
            foreach (var merchant in obtain.Merchants.Where(m => !m.Shop.StartsWith("Test_", StringComparison.Ordinal))) Add("Merchant", merchant.LevelMin);
            foreach (var fishing in obtain.Fishing) Add("Fishing", fishing.LevelMin);
        }

        if (candidates.Count == 0) return (null, null);
        var best = candidates.OrderBy(c => c.Level).ThenBy(c => c.Method, StringComparer.Ordinal).First();
        return (best.Level, best.Method);
    }

    /// <summary>The columns of every work type that at least one species has, in the order of the data bundle.</summary>
    public static IReadOnlyList<WorkerColumns> Compute(IGameData data)
    {
        var result = new List<WorkerColumns>();
        // Every species is looked at once.
        var earliest = data.Pals.ToDictionary(p => p.Id, Earliest, StringComparer.Ordinal);

        foreach (var workType in data.WorkTypes)
        {
            var entries = new List<WorkerEntry>();
            foreach (var pal in data.Pals)
            {
                if (!pal.Work.TryGetValue(workType.Id, out var level) || level <= 0) continue;
                var (earliestLevel, method) = earliest[pal.Id];
                // Not catchable and not breedable: no way to get it at all.
                if (earliestLevel == null && pal.BreedingFlags.Unbreedable) continue;
                entries.Add(new WorkerEntry(pal, level, earliestLevel, method, pal.Work.Count(w => w.Value > 0 && w.Key != workType.Id)));
            }

            if (entries.Count == 0) continue;
            var ordered = entries.OrderByDescending(e => e.WorkLevel)
                .ThenBy(e => e.EarliestLevel ?? int.MaxValue)
                .ThenByDescending(e => e.OtherWork)
                .ThenBy(e => e.Pal.DexNo).ThenBy(e => e.Pal.IsVariant).ThenBy(e => e.Pal.Id, StringComparer.Ordinal)
                .ToList();

            result.Add(new WorkerColumns(
                workType.Id,
                ordered.Where(e => e.EarliestLevel is { } l && l <= EarlyMaxLevel).Take(PerColumn).ToList(),
                ordered.Where(e => e.EarliestLevel is { } l && l <= MidMaxLevel).Take(PerColumn).ToList(),
                ordered.Take(PerColumn).ToList()));
        }

        return result;
    }
}
