using System.Globalization;

namespace Paldium.App.Services;

/// <summary>
/// Current UI language (EN/TR). Switching applies immediately: it sets the thread cultures used by
/// <c>IStringLocalizer</c>, stores the choice in the settings and raises <see cref="Changed"/> so components
/// re-render.
/// </summary>
public sealed class LanguageService
{
    public static readonly IReadOnlyList<string> Supported = ["en", "tr"];

    private readonly SettingsStore _settings;

    public LanguageService(SettingsStore settings)
    {
        _settings = settings;
        Current = Normalize(settings.Current.Language);
        Apply(Current);
    }

    /// <summary>Two-letter language code, "en" or "tr".</summary>
    public string Current { get; private set; }

    public event EventHandler? Changed;

    public void Set(string lang)
    {
        var normalized = Normalize(lang);
        if (normalized == Current) return;

        Current = normalized;
        Apply(normalized);
        _settings.Update(s => s with { Language = normalized });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string Normalize(string? lang) =>
        lang != null && lang.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en";

    private static void Apply(string lang)
    {
        var culture = CultureInfo.GetCultureInfo(lang);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
    }
}
