using System.Globalization;
using System.Text;

namespace Paldium.Core.Queries;

/// <summary>
/// Culture independent text folding for every search box (ARCHITECTURE 9): case, accents and the Turkish dotted / dotless I are ignored, so
/// "saldiri" finds "Saldırı", "SALDIRI" finds "Saldırı" and "calisma" finds "Çalışma hızı". The invariant culture alone cannot do this
/// (it keeps "ı" and "i" apart), so both sides are folded explicitly: ı, İ, I -> i, then accents are removed (ç c, ş s, ğ g, ö o, ü u).
/// </summary>
public static class TextFold
{
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // Explicit first: the invariant lower case of "İ" is not a plain "i" and "I" must not become "ı".
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            switch (c)
            {
                case 'ı' or 'İ' or 'I': builder.Append('i'); break;
                default:
                    if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(c));
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>True when the folded <paramref name="text"/> contains the folded <paramref name="query"/>.</summary>
    public static bool Contains(string? text, string? query) =>
        Fold(text).Contains(Fold(query), StringComparison.Ordinal);
}
