using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.Core.Queries;

/// <summary>What the My Pals table can be sorted by (T65).</summary>
public enum OwnedSortKey { Default, Species, Level, Gender, IvTotal, IvHp, IvAttack, IvDefense, Location }

/// <summary>Sorting and the IV filter of the owned-Pal table. A Pal whose IVs are unknown always comes last when sorting by an IV, whatever the direction.</summary>
public static class OwnedPalSort
{
    private static (int DexNo, bool IsVariant) SpeciesKey(IGameData data, string speciesId) =>
        data.TryNormalizePalId(speciesId, out var id) && data.Pal(id) is var pal
            ? (pal.DexNo, pal.IsVariant)
            : (int.MaxValue, false);

    /// <summary>The default order: dex number, variants after the base form, higher level first.</summary>
    public static IOrderedEnumerable<OwnedPal> ByDex(IEnumerable<OwnedPal> pals, IGameData data) =>
        pals.OrderBy(p => SpeciesKey(data, p.SpeciesId))
            .ThenByDescending(p => p.Level)
            .ThenBy(p => p.Uid, StringComparer.Ordinal);

    public static List<OwnedPal> Sort(IEnumerable<OwnedPal> pals, IGameData data, OwnedSortKey key, bool descending)
    {
        if (key == OwnedSortKey.Default) return ByDex(pals, data).ToList();

        // Pals without the value (unknown IVs) go last in both directions.
        if (key is OwnedSortKey.IvTotal or OwnedSortKey.IvHp or OwnedSortKey.IvAttack or OwnedSortKey.IvDefense)
        {
            var list = pals.ToList();
            int? Value(OwnedPal p) => key switch
            {
                OwnedSortKey.IvTotal => p.IvTotal,
                OwnedSortKey.IvHp => p.Ivs?.Hp,
                OwnedSortKey.IvAttack => p.Ivs?.Attack,
                _ => p.Ivs?.Defense,
            };
            var known = list.Where(p => Value(p).HasValue);
            var ordered = descending ? known.OrderByDescending(p => Value(p)) : known.OrderBy(p => Value(p));
            return ordered.ThenBy(p => SpeciesKey(data, p.SpeciesId).DexNo).ThenBy(p => p.Uid, StringComparer.Ordinal)
                .Concat(list.Where(p => !Value(p).HasValue).OrderBy(p => SpeciesKey(data, p.SpeciesId).DexNo).ThenBy(p => p.Uid, StringComparer.Ordinal)).ToList();
        }

        IOrderedEnumerable<OwnedPal> Order<TKey>(Func<OwnedPal, TKey> selector) =>
            descending ? pals.OrderByDescending(selector) : pals.OrderBy(selector);

        var sorted = key switch
        {
            OwnedSortKey.Species => Order(p => SpeciesKey(data, p.SpeciesId)),
            OwnedSortKey.Level => Order(p => p.Level),
            OwnedSortKey.Gender => Order(p => p.Gender is null ? 2 : (int)p.Gender),
            // Kind, then the box (two storages share page numbers), then page, then slot inside the page.
            _ => Order(p => (p.Location.Kind, p.Location.ContainerId ?? p.Location.BaseName ?? string.Empty, p.Location.Page ?? 0, p.Location.SlotIndex ?? 0)),
        };
        return sorted.ThenBy(p => SpeciesKey(data, p.SpeciesId).DexNo).ThenByDescending(p => p.Level).ThenBy(p => p.Uid, StringComparer.Ordinal).ToList();
    }

    /// <summary>The IV filter "total at least N": a Pal whose IVs are unknown does not pass a filter above 0.</summary>
    public static bool PassesMinIvTotal(OwnedPal pal, int? minTotal) => minTotal is not > 0 || (pal.IvTotal is { } total && total >= minTotal);
}
