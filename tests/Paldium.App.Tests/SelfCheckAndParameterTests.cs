using Microsoft.Extensions.Logging.Abstractions;
using Paldium.App.Components;
using Paldium.App.Services;

namespace Paldium.App.Tests;

public class SelfCheckAndParameterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-selfcheck-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string FindDataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(candidate, "gamedata.json"))) return candidate;
        }

        throw new DirectoryNotFoundException("data folder not found above test output.");
    }

    [Fact]
    public void AppState_Reload_RecoversWhenDataFilesBecomeAvailable()
    {
        var dataDir = Path.Combine(_dir, "data");
        Directory.CreateDirectory(dataDir);

        var state = AppState.Load(dataDir, NullLogger.Instance);
        Assert.False(state.IsReady);
        Assert.NotNull(state.Error);
        Assert.NotNull(state.ErrorDetail);

        var sourceData = FindDataDir();
        File.Copy(Path.Combine(sourceData, "gamedata.json"), Path.Combine(dataDir, "gamedata.json"));
        File.Copy(Path.Combine(sourceData, "breeding.json"), Path.Combine(dataDir, "breeding.json"));

        var changes = 0;
        state.Changed += (_, _) => changes++;

        Assert.True(state.Reload());
        Assert.True(state.IsReady);
        Assert.Null(state.Error);
        Assert.NotNull(state.Data);
        Assert.NotNull(state.Breeding);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void IconCheck_NeedsTheIconFolderWithAtLeastOneFile()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "wwwroot")); // a wwwroot without icons used to pass
        Assert.False(SelfCheck.CheckIcons(_dir).Pass);

        var icons = Path.Combine(_dir, "wwwroot", "gamedata", "icons", "pals");
        Directory.CreateDirectory(icons);
        Assert.False(SelfCheck.CheckIcons(_dir).Pass); // empty folder

        File.WriteAllBytes(Path.Combine(icons, "CowPal.png"), [1]);
        Assert.True(SelfCheck.CheckIcons(_dir).Pass);
    }

    [Fact]
    public void AQueryParameter_IsAppliedOncePerValue_SoClearedFiltersStayCleared()
    {
        var once = new OnceParameter();
        Assert.True(once.IsNew("CowPal"));   // first render: the filter is set
        Assert.False(once.IsNew("CowPal"));  // "Clear filters", next OnParametersSet: not applied again
        Assert.True(once.IsNew("Anubis"));   // a new link applies again
        Assert.True(once.IsNew(null));       // the parameter went away
        Assert.False(once.IsNew(null));
    }
}
