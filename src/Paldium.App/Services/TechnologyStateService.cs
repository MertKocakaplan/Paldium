namespace Paldium.App.Services;

/// <summary>The Technology page's search, filters and scroll position, kept while the app is open (screens/technology.md); not written to disk.</summary>
public sealed class TechnologyStateService
{
    public string Query { get; set; } = "";
    public bool AncientOnly { get; set; }
    public bool PrerequisiteOnly { get; set; }

    /// <summary>The scroll position (pixels) of the level list when the page was left.</summary>
    public double ScrollTop { get; set; }
}
