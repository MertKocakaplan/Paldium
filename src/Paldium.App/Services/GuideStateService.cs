namespace Paldium.App.Services;

/// <summary>
/// Guides state kept while the app is open (screens/guides.md): the guide language (independent of the UI language),
/// the library search and the reading mode, which survives moving between guides.
/// </summary>
public sealed class GuideStateService
{
    private bool _reading;

    /// <summary>Raised when the reading mode changes (the shell hides its tabs and the sync badge).</summary>
    public event EventHandler? Changed;

    /// <summary>"en" or "tr" when the reader chose a guide language; null follows the UI language.</summary>
    public string? GuideLang { get; set; }

    public string Query { get; set; } = "";

    // The Ranch table (T109): the view, the search, the filter and the Farming level are kept while the app is open.
    public string RanchView { get; set; } = "pal";
    public string RanchQuery { get; set; } = "";
    public bool RanchOnlyOwned { get; set; }
    public string? RanchPal { get; set; }
    public string? RanchItem { get; set; }
    public int RanchLevel { get; set; } = 1;

    /// <summary>The level came from the URL (rank=): it is used as it is, also below a Pal's own base level.</summary>
    public bool RanchLevelFromUrl { get; set; }

    // The Transporting table (T120): search, filters and sort are kept while the app is open.
    public string TransportQuery { get; set; } = "";
    public bool TransportOnlyOwned { get; set; }
    public int TransportMinLevel { get; set; } = 1;
    public string TransportSort { get; set; } = "speed";
    public bool TransportDescending { get; set; } = true;

    // The Mounts table (T124): search, kinds, the star and passive choice and the sort are kept while the app is open.
    public string MountQuery { get; set; } = "";
    public IReadOnlyList<string> MountKinds { get; set; } = [];
    public bool MountOnlyOwned { get; set; }
    public int MountStars { get; set; }
    public IReadOnlyList<string> MountPassives { get; set; } = [];
    public string MountSort { get; set; } = "sprint";
    public bool MountDescending { get; set; } = true;

    // The Merchants table (T137): the search, the selected row (an item or a shop) and the product sort are kept while the app is open.
    public string ShopQuery { get; set; } = "";
    public string? ShopSelected { get; set; }
    public bool ShopSelectedIsItem { get; set; }
    public string ShopSort { get; set; } = "name";
    public bool ShopSortDescending { get; set; }

    public bool Reading
    {
        get => _reading;
        set
        {
            if (_reading == value) return;
            _reading = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string EffectiveLang(string uiLang) => GuideLang ?? uiLang;

    /// <summary>True when a <c>/guides/&lt;slug&gt;</c> route renders one of the interactive reference tables rather than a Markdown article.</summary>
    public static bool IsReferenceSlug(string? slug) =>
        slug != null && (slug.Equals("agil", StringComparison.OrdinalIgnoreCase)
                         || slug.Equals("tasima", StringComparison.OrdinalIgnoreCase)
                         || slug.Equals("binekler", StringComparison.OrdinalIgnoreCase)
                         || slug.Equals("heykeller", StringComparison.OrdinalIgnoreCase)
                         || slug.Equals("tuccarlar", StringComparison.OrdinalIgnoreCase));
}
