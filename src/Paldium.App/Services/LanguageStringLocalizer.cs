using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;
using Paldium.App.Resources;

namespace Paldium.App.Services;

/// <summary>
/// <see cref="IStringLocalizer{Strings}"/> that always uses the language chosen in <see cref="LanguageService"/>.
/// The default localizer follows <c>CultureInfo.CurrentUICulture</c>, which does not survive Blazor's render
/// scheduling after a language switch; reading the language explicitly avoids that.
/// </summary>
public sealed class LanguageStringLocalizer : IStringLocalizer<Strings>
{
    private static readonly ResourceManager Resources =
        new("Paldium.App.Resources.Strings", typeof(Strings).Assembly);

    private readonly LanguageService _language;

    public LanguageStringLocalizer(LanguageService language)
    {
        _language = language;
    }

    private CultureInfo Culture => CultureInfo.GetCultureInfo(_language.Current);

    public LocalizedString this[string name]
    {
        get
        {
            var value = Resources.GetString(name, Culture);
            return new LocalizedString(name, value ?? name, resourceNotFound: value == null);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var template = this[name];
            var text = template.ResourceNotFound ? template.Value : string.Format(Culture, template.Value, arguments);
            return new LocalizedString(name, text, template.ResourceNotFound);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var set = Resources.GetResourceSet(Culture, createIfNotExists: true, tryParents: includeParentCultures);
        if (set == null) yield break;
        foreach (System.Collections.DictionaryEntry entry in set)
        {
            var key = (string)entry.Key;
            yield return new LocalizedString(key, entry.Value?.ToString() ?? key, resourceNotFound: false);
        }
    }
}
