using System.Globalization;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.App.Services;

/// <summary>
/// In-memory filter and sort state for My Pals while the app is open (mypals.md: not persisted to disk).
/// </summary>
public sealed class MyPalsStateService
{
    public const string SortDex = "dex";
    public const string SortName = "name";
    public const string SortLevel = "level";
    public const string SortIv = "iv";
    public const string SortLocation = "location";
    public const string SortAdded = "added";

    public OwnedPalFilter Filter { get; set; } = OwnedPalFilter.Empty;
    public OwnedSortKey SortKey { get; set; } = OwnedSortKey.Default;
    public bool Descending { get; set; }
    public string GallerySort { get; set; } = SortDex;

    /// <summary>
    /// Filters and sorts. The gallery sort (name, recently added) belongs to the card views only; the table sorts by its column
    /// (<see cref="SortKey"/> and <see cref="Descending"/>), so the arrow in a column header is the real order.
    /// </summary>
    public IReadOnlyList<OwnedPal> ApplyAll(IEnumerable<OwnedPal> pals, IGameData data, string lang, bool tableView = false)
    {
        if (tableView) return Filter.Apply(pals, data, SortKey, Descending);

        if (GallerySort == SortName)
        {
            var filtered = Filter.Apply(pals, data, OwnedSortKey.Default, false);
            var comparer = StringComparer.Create(
                CultureInfo.GetCultureInfo(string.Equals(lang, "tr", StringComparison.OrdinalIgnoreCase) ? "tr-TR" : "en-US"),
                ignoreCase: true);
            string NameOf(OwnedPal p) => data.TryNormalizePalId(p.SpeciesId, out var cid)
                ? data.Pal(cid).Names.Get(lang)
                : p.SpeciesId;

            var ordered = Descending
                ? filtered.OrderByDescending(NameOf, comparer)
                : filtered.OrderBy(NameOf, comparer);
            return ordered
                .ThenByDescending(p => p.Level)
                .ThenBy(p => p.Uid, StringComparer.Ordinal)
                .ToList();
        }

        if (GallerySort == SortAdded)
        {
            var filtered = Filter.Apply(pals, data, OwnedSortKey.Default, false);
            var ordered = Descending
                ? filtered.OrderByDescending(p => p.SeenAtUtc)
                : filtered.OrderBy(p => p.SeenAtUtc);
            return ordered
                .ThenBy(p => data.Pal(p.SpeciesId).DexNo)
                .ThenBy(p => p.Uid, StringComparer.Ordinal)
                .ToList();
        }

        return Filter.Apply(pals, data, SortKey, Descending);
    }

    public void SetGallerySort(string mode, bool descending)
    {
        GallerySort = mode;
        Descending = descending;
        SortKey = mode switch
        {
            SortDex => descending ? OwnedSortKey.Species : OwnedSortKey.Default,
            SortLevel => OwnedSortKey.Level,
            SortIv => OwnedSortKey.IvTotal,
            SortLocation => OwnedSortKey.Location,
            _ => OwnedSortKey.Default,
        };
    }

    public void SetTableSort(OwnedSortKey key, bool descending)
    {
        SortKey = key;
        Descending = descending;
        GallerySort = key switch
        {
            OwnedSortKey.Default or OwnedSortKey.Species => SortDex,
            OwnedSortKey.Level => SortLevel,
            OwnedSortKey.IvTotal or OwnedSortKey.IvHp or OwnedSortKey.IvAttack or OwnedSortKey.IvDefense => SortIv,
            OwnedSortKey.Location => SortLocation,
            _ => SortDex,
        };
    }
}
