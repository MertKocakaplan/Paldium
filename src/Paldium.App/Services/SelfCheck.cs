using System.IO;
using System.Text;
using Paldium.Core.Data;
using Paldium.Import.Saves;

namespace Paldium.App.Services;

/// <summary>
/// Headless check of an installed or published copy: <c>Paldium.exe --selfcheck &lt;report-file&gt; [world-folder]</c>.
/// It verifies the data bundle, the guide content, WebView2, libooz and, when a world folder is given, that a save is read.
/// </summary>
public static class SelfCheck
{
    /// <summary>The Pal icons are folder <c>wwwroot/gamedata/icons/pals</c> with at least one file; a missing folder fails.</summary>
    public static (bool Pass, string Detail) CheckIcons(string baseDirectory)
    {
        var folder = Path.Combine(baseDirectory, "wwwroot", "gamedata", "icons", "pals");
        if (!Directory.Exists(folder)) return (false, $"folder not found: {folder}");
        var count = Directory.EnumerateFiles(folder).Take(1).Count();
        return count > 0 ? (true, "Pal icons present") : (false, $"no icon files in {folder}");
    }

    public static int Run(string reportFile, string? worldFolder)
    {
        var report = new StringBuilder();
        var ok = true;

        void Line(string name, bool pass, string detail)
        {
            ok &= pass;
            report.AppendLine($"{(pass ? "OK  " : "FAIL")} {name}: {detail}");
        }

        var data = Path.Combine(AppContext.BaseDirectory, "data");
        GameData? game = null;
        try
        {
            game = GameDataLoader.Load(data);
            Line("data", true, $"{game.Pals.Count} pals, {game.Passives.Count} passives");
        }
        catch (Exception ex)
        {
            Line("data", false, ex.Message);
        }

        var guides = Path.Combine(AppContext.BaseDirectory, "content", "guides");
        var guideCount = Directory.Exists(Path.Combine(guides, "en")) ? Directory.GetFiles(Path.Combine(guides, "en"), "*.md").Length : 0;
        Line("guides", guideCount > 0, $"{guideCount} guides (en)");

        var (iconsOk, iconsDetail) = CheckIcons(AppContext.BaseDirectory);
        Line("icons", iconsOk, iconsDetail);

        var webView = WebView2Check.InstalledVersion();
        Line("webview2", webView != null, webView ?? "runtime not found");
        Line("libooz", SaveWorldReader.IsDecompressionAvailable, SaveWorldReader.IsDecompressionAvailable ? "loaded" : "not loaded (Visual C++ x64 redistributable missing?)");

        if (worldFolder != null && game != null)
        {
            var work = Path.Combine(Path.GetTempPath(), "paldium-selfcheck");
            try
            {
                var snapshot = new SaveWorldReader(game, work).ReadWorld(worldFolder);
                Line("save", snapshot.Pals.Count > 0, $"{snapshot.Pals.Count} pals read from {worldFolder}");
            }
            catch (Exception ex)
            {
                Line("save", false, ex.Message);
            }
            finally
            {
                // The copy of the save files is only needed while reading.
                try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover temp folder is harmless */ }
            }
        }

        File.WriteAllText(reportFile, report.ToString());
        return ok ? 0 : 1;
    }
}
