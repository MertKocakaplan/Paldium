using System.Globalization;
using Paldium.Core.Domain;

namespace Paldium.Core.Queries;

/// <summary>Text matching shared by the Pal picker and the Paldex search (English and Turkish names, dex number).</summary>
public static class PalTextSearch
{
    /// <summary>
    /// True when the query is empty, is contained in any language's name (case, accent and Turkish i/ı insensitive, see <see cref="TextFold"/>),
    /// or matches the dex number or dex label ("40", "94B").
    /// </summary>
    public static bool Matches(PalSpecies pal, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();

        if (int.TryParse(q, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && pal.DexNo == number)
            return true;
        if (pal.DexLabel.Equals(q, StringComparison.OrdinalIgnoreCase)) return true;

        var folded = TextFold.Fold(q);
        foreach (var lang in pal.Names.Languages)
        {
            if (TextFold.Fold(pal.Names.Get(lang)).Contains(folded, StringComparison.Ordinal)) return true;
        }

        return TextFold.Fold(pal.Id).Contains(folded, StringComparison.Ordinal);
    }
}
