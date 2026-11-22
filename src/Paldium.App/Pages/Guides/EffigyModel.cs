using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.App.Pages.GuidesParts;

/// <summary>
/// The player's progress on one effigy type (T133). <see cref="Have"/> is unspent, <see cref="Collected"/> everything found (spent + unspent); either can be
/// unknown. The level is only known when both are: spent = collected - have, and the level is the highest rank whose cumulative cost fits into it.
/// <see cref="AtLeast"/> marks a spent count that is not on a rank's cost (not expected: it means a count is short).
/// </summary>
public sealed record EffigyProgress(int? Have, int? Collected, int? Level, int MaxLevel, bool AtLeast, int? NextNeed, int? MaxNeed, double Filled)
{
    public bool Maxed => Level is { } l && l >= MaxLevel;

    /// <summary>Enough effigies are in hand for the next rank (the game's statue would accept them now).</summary>
    public bool CanRankUp => Level != null && !Maxed && NextNeed == 0;
}

/// <summary>One line on the progress bar of an effigy type: where a rank ends (share of the whole bar, 0 to 1) and which rank it is.</summary>
public sealed record EffigyTick(int Rank, double Position);

/// <summary>
/// The Pal Effigy table (screens/guides.md "Heykeller: Pal Effigy ilerlemesi", T126; the game-data part only, no player progress): the 13 enhancement types
/// with their effect, rank costs and totals, and the lines of the staged progress bar. Pure functions over the data bundle, tested without the page.
/// </summary>
public static class EffigyModel
{
    /// <summary>Width the line spacing is judged at: below <see cref="MinTickGap"/> px between two lines, only every fifth rank gets one.</summary>
    public const double NominalBarWidth = 420;

    public const double MinTickGap = 3;

    public const int ThinnedStep = 5;

    /// <summary>The types in the game's order (the statue's menu: capture power first, movement speed last).</summary>
    public static IReadOnlyList<RelicInfo> Rows(IEnumerable<RelicInfo> relics) => relics.OrderBy(r => r.Index).ToList();

    /// <summary>Effigies needed up to and including each rank (the line positions of the bar are these over the total).</summary>
    public static IReadOnlyList<int> Cumulative(RelicInfo relic)
    {
        var sum = 0;
        return relic.Ranks.OrderBy(r => r.Rank).Select(r => sum += r.Required).ToList();
    }

    /// <summary>
    /// The lines of the bar: one at the end of every rank (the last one is the bar's end and is not drawn). When two neighbouring lines would be closer than
    /// <see cref="MinTickGap"/> px on a bar of <paramref name="barWidth"/> px, only the ranks that divide by <see cref="ThinnedStep"/> keep a line.
    /// </summary>
    public static IReadOnlyList<EffigyTick> Ticks(RelicInfo relic, double barWidth = NominalBarWidth)
    {
        var total = relic.Total;
        if (total <= 0 || relic.Ranks.Count < 2) return [];
        var ranks = relic.Ranks.OrderBy(r => r.Rank).ToList();
        var cumulative = Cumulative(relic);
        var all = ranks.Select((r, i) => new EffigyTick(r.Rank, cumulative[i] / (double)total)).Where(t => t.Rank < ranks[^1].Rank).ToList();
        var smallestGap = ranks.Take(ranks.Count - 1).Min(r => r.Required) / (double)total * barWidth;
        return smallestGap >= MinTickGap ? all : all.Where(t => t.Rank % ThinnedStep == 0).ToList();
    }

    /// <summary>
    /// The progress from what the source gave. <paramref name="mimogSpecies"/> is the number of species caught 5 times (the Mimog effigy is not found in the
    /// world: one is earned per such species). Null when the source has nothing for the type.
    /// </summary>
    public static EffigyProgress? Progress(RelicInfo relic, RelicCount? count, int mimogSpecies)
    {
        var have = count?.Have;
        var collected = relic.Type == "MoveSpeed" ? (count == null ? null : mimogSpecies) : count?.Collected;
        if (have == null && collected == null) return null;

        var total = relic.Total;
        var max = relic.Ranks.Count;
        var filled = collected is { } c && total > 0 ? Math.Clamp(c / (double)total, 0, 1) : 0;
        int? maxNeed = collected is { } got ? Math.Max(0, total - got) : null;
        if (have == null || collected == null) return new EffigyProgress(have, collected, null, max, false, null, maxNeed, filled);

        var cumulative = Cumulative(relic);
        var spent = Math.Max(0, collected.Value - have.Value);
        var level = cumulative.Count(x => x <= spent);
        var onRank = spent == 0 || cumulative.Contains(spent) || level >= max;
        int? next = level < max ? Math.Max(0, cumulative[level] - collected.Value) : null;
        return new EffigyProgress(have, collected, level, max, !onRank, next, maxNeed, filled);
    }

    /// <summary>
    /// The counts of one type from a record. A type the source does not list is none held (and none collected) when the source knows the counts of the
    /// other types at all; null when the record has no effigy data.
    /// </summary>
    public static RelicCount? CountFor(CaptureRecord? record, string type)
    {
        if (record == null) return null;
        if (record.Relics is { Count: > 0 } relics)
        {
            if (relics.TryGetValue(type, out var count)) return count;
            foreach (var (key, value) in relics)
                if (string.Equals(key, type, StringComparison.OrdinalIgnoreCase)) return value;
            return new RelicCount(relics.Values.Any(r => r.Have != null) ? 0 : null, relics.Values.Any(r => r.Collected != null) ? 0 : null);
        }

        if (string.Equals(type, "MoveSpeed", StringComparison.OrdinalIgnoreCase)
            && (record.Bonus.Count > 0 || record.Count.Count > 0 || record.Relics != null))
            return new RelicCount(null, null);

        return null;
    }

    /// <summary>The effect (percent) of a rank (0 = none yet), or null when the game table has no value.</summary>
    public static double? EffectAt(RelicInfo relic, int level)
    {
        if (level <= 0) return 0;
        var rank = relic.Ranks.OrderBy(r => r.Rank).ElementAtOrDefault(level - 1);
        return rank is { Effect: > 0 } ? rank.Effect : null;
    }

    /// <summary>The effect (percent) at the highest rank, or null when the game table has no value (capture power).</summary>
    public static double? MaxEffect(RelicInfo relic)
    {
        var last = relic.Ranks.OrderBy(r => r.Rank).LastOrDefault();
        return last is { Effect: > 0 } ? last.Effect : null;
    }

    /// <summary>"17.5" or "50": at most two decimals, the culture's separator, no trailing zeros.</summary>
    public static string Percent(double value, System.Globalization.CultureInfo culture) => value.ToString("0.##", culture);
}
