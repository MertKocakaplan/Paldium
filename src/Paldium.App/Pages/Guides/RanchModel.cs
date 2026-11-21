using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.App.Pages.GuidesParts;

/// <summary>An item some Pal drops at the Ranch, with the Pals that drop it (T109).</summary>
public sealed record RanchItem(string ItemId, LocalizedText Names, string? IconPath, IReadOnlyList<PalSpecies> Pals);

/// <summary>
/// The Ranch reference table (screens/guides.md "Ağıl: kim ne düşürür"): who drops what, the chance of each item and the amount by Farming
/// level 1 to 10, from <see cref="PalSpecies.Ranch"/>. Pure functions over the data bundle, tested without the page.
/// </summary>
public static class RanchModel
{
    public const int MaxLevel = 10;

    /// <summary>Species that drop something at the Ranch, in Pal number order (a variant after its main species).</summary>
    public static IReadOnlyList<PalSpecies> Pals(IEnumerable<PalSpecies> all) =>
        all.Where(p => p.Ranch.Count > 0).OrderBy(p => p.DexNo).ThenBy(p => p.IsVariant).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

    /// <summary>Every item dropped at the Ranch with its Pals, by item name.</summary>
    public static IReadOnlyList<RanchItem> Items(IEnumerable<PalSpecies> ranchPals, string lang)
    {
        var pals = ranchPals.ToList();
        var result = new List<RanchItem>();
        foreach (var group in pals.SelectMany(p => p.Ranch.SelectMany(r => r.Drops).Select(d => (Pal: p, Drop: d))).GroupBy(x => x.Drop.ItemId, StringComparer.Ordinal))
        {
            var first = group.First().Drop;
            result.Add(new RanchItem(group.Key, first.Names, first.IconPath, group.Select(x => x.Pal).Distinct().ToList()));
        }

        var culture = System.Globalization.CultureInfo.GetCultureInfo(lang);
        return result.OrderBy(i => i.Names.Get(lang), StringComparer.Create(culture, ignoreCase: true)).ToList();
    }

    /// <summary>The items of a Pal in the order they first show up over the levels (the common ones first, the ones a higher level adds after).</summary>
    public static IReadOnlyList<RanchDrop> ItemsOf(PalSpecies pal)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<RanchDrop>();
        foreach (var rank in pal.Ranch.OrderBy(r => r.Rank))
            foreach (var drop in rank.Drops.OrderByDescending(d => d.Weight))
                if (seen.Add(drop.ItemId)) list.Add(drop);
        return list;
    }

    public static RanchDrop? DropAt(PalSpecies pal, int level, string itemId) =>
        pal.Ranch.FirstOrDefault(r => r.Rank == level)?.Drops.FirstOrDefault(d => string.Equals(d.ItemId, itemId, StringComparison.Ordinal));

    /// <summary>The first level at which the Pal drops the item (null when it never does).</summary>
    public static int? FirstLevel(PalSpecies pal, string itemId) =>
        pal.Ranch.OrderBy(r => r.Rank).FirstOrDefault(r => r.Drops.Any(d => d.ItemId == itemId))?.Rank;

    /// <summary>The amount per drop one expects: chance × the middle of the amount range.</summary>
    public static double Average(RanchDrop drop) => drop.Chance * (drop.Min + drop.Max) / 2.0;

    public static bool SingleItem(PalSpecies pal) => ItemsOf(pal).Count == 1;

    /// <summary>The Pals whose name or one of whose items matches the text (letter case and Turkish characters do not matter).</summary>
    public static IReadOnlyList<PalSpecies> SearchPals(IEnumerable<PalSpecies> ranchPals, string query, string lang)
    {
        var q = TextFold.Fold(query).Trim();
        if (q.Length == 0) return ranchPals.ToList();
        return ranchPals.Where(p => TextFold.Fold(p.Names.Get(lang)).Contains(q, StringComparison.Ordinal) ||
                                    ItemsOf(p).Any(d => TextFold.Fold(d.Names.Get(lang)).Contains(q, StringComparison.Ordinal))).ToList();
    }

    public static IReadOnlyList<RanchItem> SearchItems(IEnumerable<RanchItem> items, string query, string lang)
    {
        var q = TextFold.Fold(query).Trim();
        return q.Length == 0 ? items.ToList() : items.Where(i => TextFold.Fold(i.Names.Get(lang)).Contains(q, StringComparison.Ordinal)).ToList();
    }
}
