namespace Paldium.Core.Data;

/// <summary>
/// Text in several languages. Lookup order: requested language, its neutral part ("tr-TR" -> "tr"),
/// then "en", then the fallback (usually the internal id). "-" and blank values count as missing,
/// because PalCalc marks untranslated strings with "-".
/// </summary>
public sealed class LocalizedText
{
    private readonly Dictionary<string, string> _values;
    private readonly string _fallback;

    public LocalizedText(IEnumerable<KeyValuePair<string, string>> values, string fallback)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (lang, text) in values)
            if (!IsMissing(text)) _values[lang] = text;
        _fallback = fallback;
    }

    /// <summary>Languages that have a usable value.</summary>
    public IReadOnlyCollection<string> Languages => _values.Keys;

    public string Get(string lang)
    {
        if (TryGet(lang, out var text)) return text;
        if (_values.TryGetValue("en", out var en)) return en;
        return _fallback;
    }

    /// <summary>Same as <see cref="Get"/> but reports whether the requested language itself had a value.</summary>
    public bool TryGet(string lang, out string text)
    {
        if (!string.IsNullOrEmpty(lang))
        {
            if (_values.TryGetValue(lang, out text!)) return true;
            var dash = lang.IndexOf('-');
            if (dash > 0 && _values.TryGetValue(lang[..dash], out text!)) return true;
        }

        text = string.Empty;
        return false;
    }

    public override string ToString() => Get("en");

    private static bool IsMissing(string? text) => string.IsNullOrWhiteSpace(text) || text == "-";
}
