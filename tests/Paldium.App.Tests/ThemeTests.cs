using System.Globalization;
using System.Text.RegularExpressions;
using Paldium.App.Theme;

namespace Paldium.App.Tests;

public class ThemeTests
{
    private static readonly string AppProject = FindAppProject();

    private static string FindAppProject()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Paldium.App");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("src/Paldium.App not found above the test output.");
    }

    private static double Luminance(string hex)
    {
        double Channel(int shift)
        {
            var c = ((int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber) >> shift) & 0xFF) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(16) + 0.7152 * Channel(8) + 0.0722 * Channel(0);
    }

    private static double Contrast(string a, string b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void MudBlazorLayers_AreDefinedInRoot_AndDialogsStayAboveThePopoversAndTheAppsMenus()
    {
        // No MudThemeProvider since T79: without these, MudBlazor's dialog z-index is invalid and a message box falls behind the page (T122).
        var css = File.ReadAllText(Path.Combine(AppProject, "wwwroot", "css", "app.css"));
        int Z(string name)
        {
            var m = Regex.Match(css, @"--mud-zindex-" + name + @":\s*(\d+)");
            Assert.True(m.Success, name);
            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        foreach (var name in new[] { "drawer", "popover", "appbar", "dialog", "select", "snackbar", "tooltip" }) Z(name);
        const int appMenuOverlay = 1100;   // MudOverlay ZIndex of the app's own drop-downs and pickers
        Assert.True(Z("popover") > appMenuOverlay);
        Assert.True(Z("dialog") > Z("popover") && Z("snackbar") > Z("dialog") && Z("tooltip") > Z("snackbar"));
    }

    [Fact]
    public void TheGrafitTokens_MeetContrastTargets()
    {
        var css = File.ReadAllText(Path.Combine(AppProject, "wwwroot", "css", "app.css"));
        string Token(string name)
        {
            var match = Regex.Match(css, $@"{Regex.Escape(name)}:\s*(#[0-9a-fA-F]{{6}})\b");
            Assert.True(match.Success, $"Missing token {name}");
            return match.Groups[1].Value;
        }

        var surface = Token("--surface");
        var raised = Token("--raised");
        var ink = Token("--ink");
        var muted = Token("--muted");
        var primary = Token("--primary");
        var onPrimary = Token("--on-primary");

        Assert.True(Contrast(ink, surface) >= 13);
        Assert.True(Contrast(muted, raised) >= 6);
        Assert.True(Contrast(onPrimary, primary) >= 13);
    }

    [Fact]
    public void TheTheme_UsesIbmPlexFonts_AndNoPurpleOrLegacyTokens()
    {
        var theme = PaldiumTheme.Value;
        Assert.Contains("'IBM Plex Sans'", theme.Typography.H1.FontFamily!);
        Assert.DoesNotContain("'Baloo 2'", theme.Typography.H1.FontFamily!);
        Assert.Contains("'IBM Plex Mono'", theme.Typography.H5.FontFamily!);

        var css = File.ReadAllText(Path.Combine(AppProject, "wwwroot", "css", "app.css"));
        Assert.DoesNotContain("--pb-", css, StringComparison.Ordinal);
        Assert.DoesNotContain("Baloo", css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("776be7", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheStylesheet_KeepsItsColorLiteralsInTheTokenSection()
    {
        var css = File.ReadAllText(Path.Combine(AppProject, "wwwroot", "css", "app.css"));
        var start = css.IndexOf("== 2. Tokenlar ==", StringComparison.Ordinal);
        var end = css.IndexOf("== 3. Temel ==", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var outside = css[..start] + css[end..];
        outside = Regex.Replace(outside, @"unicode-range:[^;]*;", "");
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b", outside);
        Assert.DoesNotMatch(@"rgba?\(", outside);
        Assert.DoesNotContain("776be7", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheFontFiles_AndTheirLicenses_AreShipped()
    {
        Assert.False(Directory.Exists(Path.Combine(AppProject, "wwwroot", "fonts", "baloo-2")));

        var dir = Path.Combine(AppProject, "wwwroot", "fonts", "ibm-plex");
        Assert.True(File.Exists(Path.Combine(dir, "ibm-plex-sans-latin.woff2")));
        Assert.True(File.Exists(Path.Combine(dir, "ibm-plex-sans-latin-ext.woff2")));
        Assert.True(File.Exists(Path.Combine(dir, "ibm-plex-mono-latin-400.woff2")));
        Assert.True(File.Exists(Path.Combine(dir, "ibm-plex-mono-latin-ext-400.woff2")));
        Assert.True(File.Exists(Path.Combine(dir, "ibm-plex-mono-latin-500.woff2")));
        Assert.True(File.Exists(Path.Combine(dir, "ibm-plex-mono-latin-ext-500.woff2")));
        Assert.True(File.Exists(Path.Combine(dir, "LICENSE-ibm-plex-sans.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "LICENSE-ibm-plex-mono.txt")));
    }
}
