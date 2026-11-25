using System.Globalization;
using System.IO;
using Microsoft.Extensions.Localization;
using Paldium.App.Resources;
using Paldium.App.Services;
using Paldium.Core.Queries;

namespace Paldium.App.Pages.PaldexParts;

/// <summary>The dropped-item search of the Paldex (T152): its link, the "Drop" lines of a species and the sort that comes with the filter.</summary>
public static class DropModel
{
    public static string Link(string itemId, PalDropSource source) =>
        "paldex?drop=" + Uri.EscapeDataString(itemId) + (source == PalDropSource.Any ? string.Empty : "&dropsrc=" + source.ToString().ToLowerInvariant());

    /// <summary>The <c>?dropsrc=</c> of a link; anything else than "defeat" or "ranch" means every source.</summary>
    public static PalDropSource ParseSource(string? text) => text?.ToLowerInvariant() switch
    {
        "defeat" => PalDropSource.Defeat,
        "ranch" => PalDropSource.Ranch,
        _ => PalDropSource.Any,
    };

    /// <summary>Same rounding as the "Dropped items" list of the Pal detail.</summary>
    public static string Percent(double value, string lang) => value.ToString(value < 0.01 ? "P1" : "P0", CultureInfo.GetCultureInfo(lang));

    public static string Amount(int min, int max) =>
        min == max ? min.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{min}–{max}");

    /// <summary>The ending of "from" after a Ranch level (1 to 10) in Turkish: 5'ten, 6'dan, 7'den.</summary>
    public static string Ablative(int number) => number switch
    {
        3 or 4 or 5 => "ten",
        6 or 9 or 10 => "dan",
        _ => "den",
    };

    /// <summary>One line per way the species drops the item, the defeat drop first.</summary>
    public static IReadOnlyList<string> Lines(PalDrop drop, string lang, IStringLocalizer<Strings> l)
    {
        var lines = new List<string>(2);
        if (drop.Defeat is { } d)
        {
            var level = d.MinLevel > 0 ? l["Drop.MinLevel", d.MinLevel].Value + " " : string.Empty;
            lines.Add(level + Amount(d.Min, d.Max) + " · " + Percent(d.Rate, lang));
        }

        if (drop.Ranch is { } r) lines.Add(l["Drop.RanchFrom", r.Rank, Ablative(r.Rank)].Value + " · " + Percent(r.Drop.Chance, lang));
        return lines;
    }

    /// <summary>
    /// The settings after a filter change. Choosing an item selects the chance sort and remembers the sort before it; removing the item brings
    /// that sort back, unless the user has chosen another one in the meantime.
    /// </summary>
    public static PaldexSettings Apply(PaldexSettings state, PalFilter filter)
    {
        var before = string.IsNullOrWhiteSpace(state.Filter.DropItem) ? null : state.Filter.DropItem;
        var after = string.IsNullOrWhiteSpace(filter.DropItem) ? null : filter.DropItem;
        var next = state with { Filter = filter };

        if (after != null && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            var remember = state.Sort.Key == PalSortKey.DropChance ? state.SortBeforeDrop ?? new PalSort() : before == null ? state.Sort : state.SortBeforeDrop;
            return next with { Sort = new PalSort { Key = PalSortKey.DropChance, Descending = true }, SortBeforeDrop = remember };
        }

        if (after == null && (before != null || state.Sort.Key == PalSortKey.DropChance))
        {
            var sort = state.Sort.Key == PalSortKey.DropChance ? state.SortBeforeDrop ?? new PalSort() : state.Sort;
            return next with { Sort = sort, SortBeforeDrop = null };
        }

        return next;
    }

    /// <summary>The picture of an item as a page URL, or null when the data has none (the place then stays empty).</summary>
    public static string? IconUrl(string itemId, string? iconPath, string? baseDirectory = null)
    {
        var relative = iconPath ?? "icons/items/" + itemId + ".png";
        return File.Exists(Path.Combine(baseDirectory ?? AppContext.BaseDirectory, "wwwroot", "gamedata", relative)) ? "gamedata/" + relative : null;
    }
}
