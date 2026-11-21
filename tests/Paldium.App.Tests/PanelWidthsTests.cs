using Paldium.App.Services;

namespace Paldium.App.Tests;

public class PanelWidthsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-panel-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void Width_IsClampedAndStoredPerPage()
    {
        var path = Path.Combine(_dir, "settings.json");
        var widths = new PanelWidths(new SettingsStore(path));
        Assert.Equal(288, widths.Get("mypals"));
        Assert.Equal(480, widths.Set("mypals", 900));
        Assert.Equal(200, widths.Set("skills", 50));
        Assert.Equal(300, widths.Set("planner", 300));
        Assert.Equal(480, widths.Get("mypals"));
        Assert.Equal(288, widths.Get("paldex"));

        // Stored: a new store reading the same file sees the widths.
        var again = new PanelWidths(new SettingsStore(path));
        Assert.Equal(300, again.Get("planner"));
        Assert.Equal(200, again.Get("skills"));
    }
}
