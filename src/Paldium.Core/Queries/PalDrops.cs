using System.Globalization;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Queries;

/// <summary>Where a drop comes from (T152).</summary>
public enum PalDropSource
{
    Any,

    /// <summary>Dropped when the Pal is defeated.</summary>
    Defeat,

    /// <summary>Produced at the Ranch.</summary>
    Ranch,
}

/// <summary>An item at least one Pal drops, with the number of species that drop it (either way).</summary>
public sealed record DroppedItem(string ItemId, LocalizedText Names, string? IconPath, int PalCount);

/// <summary>The first Ranch level at which a Pal produces an item, with the chance and the amount at that level.</summary>
public sealed record RanchDropAt(int Rank, RanchDrop Drop);

/// <summary>How one species drops one item: on defeat, at the Ranch, both or neither.</summary>
public sealed record PalDrop(DropInfo? Defeat, RanchDropAt? Ranch)
{
    public bool Found => Defeat != null || Ranch != null;

    /// <summary>The better chance of the two with the amount that goes with it (minimum plus maximum), for the chance sort; (-1, 0) when neither.</summary>
    public (double Chance, int Amount) Best
    {
        get
        {
            (double Chance, int Amount) best = Defeat == null ? (-1.0, 0) : (Defeat.Rate, Defeat.Min + Defeat.Max);
            if (Ranch?.Drop is { } r && r.Chance.CompareTo(best.Chance) > 0) best = (r.Chance, r.Min + r.Max);
            return best;
        }
    }
}

/// <summary>
/// "Which Pals drop this item" (T152), from <see cref="PalSpecies.Drops"/> and <see cref="PalSpecies.Ranch"/>. Item ids are compared without
/// letter case (the game's own names are case-insensitive and the data spells one id two ways).
/// </summary>
public static class PalDrops
{
    /// <summary>The shortest text the suggestion row reacts to: shorter ones are part of almost every name.</summary>
    public const int SuggestMinLength = 3;

    private static readonly string[] Languages = ["en", "tr"];

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static PalDrop Of(PalSpecies pal, string itemId, PalDropSource source)
    {
        var defeat = source == PalDropSource.Ranch ? null : pal.Drops.FirstOrDefault(d => Same(d.ItemId, itemId));
        RanchDropAt? ranch = null;
        if (source != PalDropSource.Defeat)
        {
            foreach (var rank in pal.Ranch.OrderBy(r => r.Rank))
            {
                if (rank.Drops.FirstOrDefault(d => Same(d.ItemId, itemId)) is not { } drop) continue;
                ranch = new RanchDropAt(rank.Rank, drop);
                break;
            }
        }

        return new PalDrop(defeat, ranch);
    }

    /// <summary>Every dropped item, the ones most species drop first, then by name in <paramref name="lang"/>.</summary>
    public static IReadOnlyList<DroppedItem> Items(IEnumerable<PalSpecies> pals, string lang)
    {
        var all = pals.SelectMany(p =>
            p.Drops.Select(d => (Pal: p.Id, d.ItemId, d.Names, IconPath: (string?)null))
                .Concat(p.Ranch.SelectMany(r => r.Drops).Select(d => (Pal: p.Id, d.ItemId, d.Names, d.IconPath))));

        var names = StringComparer.Create(CultureInfo.GetCultureInfo(lang), ignoreCase: true);
        return all.GroupBy(x => x.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DroppedItem(
                g.Select(x => x.ItemId).Order(StringComparer.Ordinal).First(),
                g.First().Names,
                g.Select(x => x.IconPath).FirstOrDefault(path => path != null),
                g.Select(x => x.Pal).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(i => i.PalCount)
            .ThenBy(i => i.Names.Get(lang), names)
            .ThenBy(i => i.ItemId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The item with the id (from a link or the stored filter), or null.</summary>
    public static DroppedItem? Find(IEnumerable<DroppedItem> items, string? itemId) =>
        string.IsNullOrWhiteSpace(itemId) ? null : items.FirstOrDefault(i => Same(i.ItemId, itemId));

    /// <summary>The items whose English or Turkish name contains the text (see <see cref="TextFold"/>), in the order given; all for an empty text.</summary>
    public static IReadOnlyList<DroppedItem> Search(IEnumerable<DroppedItem> items, string? query)
    {
        var q = TextFold.Fold(query).Trim();
        return q.Length == 0 ? items.ToList() : items.Where(i => Rank(i, q) != null).ToList();
    }

    /// <summary>
    /// The one item the species search suggests for the text: an exact name first, then a name starting with it, a word starting with it and any
    /// other match. Null for a short or unknown text.
    /// </summary>
    public static DroppedItem? Suggest(IEnumerable<DroppedItem> items, string? query)
    {
        var q = TextFold.Fold(query).Trim();
        if (q.Length < SuggestMinLength) return null;
        // OrderBy is stable: among equals the item more species drop stays first.
        return items.Select(i => (Item: i, Rank: Rank(i, q))).Where(x => x.Rank != null).OrderBy(x => x.Rank).Select(x => x.Item).FirstOrDefault();
    }

    private static int? Rank(DroppedItem item, string foldedQuery)
    {
        int? best = null;
        foreach (var lang in Languages)
        {
            if (!item.Names.TryGet(lang, out var text)) continue;
            var name = TextFold.Fold(text);
            var at = name.IndexOf(foldedQuery, StringComparison.Ordinal);
            if (at < 0) continue;
            var rank = name.Length == foldedQuery.Length ? 0
                : at == 0 ? 1
                : name.Contains(" " + foldedQuery, StringComparison.Ordinal) ? 2
                : 3;
            if (best == null || rank < best) best = rank;
        }

        return best;
    }
}
