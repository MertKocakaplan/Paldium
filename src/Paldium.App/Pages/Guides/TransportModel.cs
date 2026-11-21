using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.App.Pages.GuidesParts;

/// <summary>One line of the Transporting table: a Pal that has the Transporting work, its level and speeds (T120).</summary>
public sealed record TransportRow(PalSpecies Pal, int Level, int TransportSpeed, int RunSpeed);

/// <summary>
/// The Transporting reference table (screens/guides.md "Taşıma: kim en hızlı taşır"): every Pal with the Transporting work (level 1 to 7), its
/// carrying speed and run speed from the game data, sorted and filtered. Pure functions over the data bundle, tested without the page.
/// </summary>
public static class TransportModel
{
    public const string WorkKey = "Transporting";
    public const int MaxLevel = 7;

    public const string SortPal = "pal", SortLevel = "level", SortSpeed = "speed", SortRun = "run", SortOwned = "owned";

    /// <summary>The Pals with the Transporting work, with their numbers.</summary>
    public static IReadOnlyList<TransportRow> Rows(IEnumerable<PalSpecies> all) =>
        all.Where(p => p.Work.TryGetValue(WorkKey, out var level) && level > 0)
           .Select(p => new TransportRow(p, p.Work[WorkKey], p.Stats.TransportSpeed, p.Stats.RunSpeed)).ToList();

    /// <summary>
    /// Search by Pal name (letter case and Turkish characters do not matter), a minimum Transporting level and "only the ones you have".
    /// </summary>
    public static IReadOnlyList<TransportRow> Filter(IEnumerable<TransportRow> rows, string query, string lang, int minLevel, bool onlyOwned, Func<string, int> ownedOf)
    {
        var q = TextFold.Fold(query).Trim();
        return rows.Where(r => r.Level >= minLevel
                               && (q.Length == 0 || TextFold.Fold(r.Pal.Names.Get(lang)).Contains(q, StringComparison.Ordinal))
                               && (!onlyOwned || ownedOf(r.Pal.Id) > 0)).ToList();
    }

    /// <summary>
    /// Sorted by a column. The default order of the page is carrying speed, high first; every sort breaks ties by speed, then level, then Pal
    /// number, so the same input always gives the same list.
    /// </summary>
    public static IReadOnlyList<TransportRow> Sort(IEnumerable<TransportRow> rows, string key, bool descending, string lang, Func<string, int> ownedOf)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(lang);
        IOrderedEnumerable<TransportRow> ordered = key switch
        {
            SortPal => Direction(rows, r => r.Pal.DexNo, descending).ThenBy(r => r.Pal.IsVariant).ThenBy(r => r.Pal.Id, StringComparer.Ordinal),
            SortLevel => Direction(rows, r => r.Level, descending).ThenByDescending(r => r.TransportSpeed),
            SortRun => Direction(rows, r => r.RunSpeed, descending).ThenByDescending(r => r.TransportSpeed),
            SortOwned => Direction(rows, r => ownedOf(r.Pal.Id), descending).ThenByDescending(r => r.TransportSpeed),
            _ => Direction(rows, r => r.TransportSpeed, descending).ThenByDescending(r => r.Level),
        };
        return ordered.ThenBy(r => r.Pal.DexNo).ThenBy(r => r.Pal.IsVariant).ThenBy(r => r.Pal.Names.Get(lang), StringComparer.Create(culture, ignoreCase: true)).ToList();
    }

    private static IOrderedEnumerable<TransportRow> Direction<T>(IEnumerable<TransportRow> rows, Func<TransportRow, T> key, bool descending) =>
        descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    /// <summary>The direction a column starts with when it is chosen: numbers high first, the Pal column by number up.</summary>
    public static bool DefaultDescending(string key) => key != SortPal;
}
