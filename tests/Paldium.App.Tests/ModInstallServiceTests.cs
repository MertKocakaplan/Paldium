using Paldium.App.Services;

namespace Paldium.App.Tests;

public class ModInstallServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-mod-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _game;
    private bool _running;

    public ModInstallServiceTests()
    {
        _source = Path.Combine(_dir, "source");
        Directory.CreateDirectory(Path.Combine(_source, "Scripts"));
        File.WriteAllText(Path.Combine(_source, "Scripts", "main.lua"), "print('x')");
        File.WriteAllText(Path.Combine(_source, "enabled.txt"), "");
        File.WriteAllText(Path.Combine(_source, "Info.json"), "{}");
        _game = Path.Combine(_dir, "lib", "steamapps", "common", "Palworld");
        Directory.CreateDirectory(Path.Combine(_game, "Pal", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(_game, "Pal", "Binaries", "Win64", "Palworld-Win64-Shipping.exe"), "");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ModInstallService NewService() =>
        new(_source, () => [Path.Combine(_dir, "lib"), Path.Combine(_dir, "nothing")], () => _running);

    [Fact]
    public void GameFolder_IsFoundInTheSteamLibraries_OrTakenFromTheSavedValue()
    {
        Assert.Equal(_game, NewService().FindGameFolder(null));
        Assert.Equal(_game, NewService().FindGameFolder(Path.Combine(_dir, "stale")));
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(Path.Combine(other, "Pal"));
        Assert.Equal(other, NewService().FindGameFolder(other));
    }

    [Fact]
    public void Install_RemovesTheModOfTheFormerName_AndOnlyItsEntries()
    {
        var mods = Path.Combine(_game, "Mods", "NativeMods", "UE4SS", "Mods");
        var old = Path.Combine(mods, "PalbreedingBridge");
        Directory.CreateDirectory(Path.Combine(old, "Scripts"));
        File.WriteAllText(Path.Combine(old, "Scripts", "main.lua"), "old");
        File.WriteAllText(Path.Combine(mods, "mods.txt"), "Other : 1\nPalbreedingBridge : 1\nThird : 0\n");
        File.WriteAllText(Path.Combine(mods, "mods.json"), "[{\"mod_name\":\"Other\",\"mod_enabled\":true},{\"mod_name\":\"PalbreedingBridge\",\"mod_enabled\":true}]");
        var service = NewService();
        Assert.Equal(ModInstallState.LegacyInstalled, service.GetState(_game));

        service.Install(_game);

        Assert.False(Directory.Exists(old));
        Assert.Equal(["Other : 1", "Third : 0"], File.ReadAllLines(Path.Combine(mods, "mods.txt")));
        var json = File.ReadAllText(Path.Combine(mods, "mods.json"));
        Assert.Contains("Other", json);
        Assert.DoesNotContain("Palbreeding", json);
        Assert.True(Directory.Exists(Path.Combine(mods, "PaldiumBridge")));
        Assert.Equal(ModInstallState.Current, service.GetState(_game));
    }

    [Fact]
    public void BothModsInstalled_CountAsOutdated_UntilTheOldOneIsGone()
    {
        var mods = Path.Combine(_game, "Mods", "NativeMods", "UE4SS", "Mods");
        Directory.CreateDirectory(mods);
        var service = NewService();
        service.Install(_game);
        Directory.CreateDirectory(Path.Combine(mods, "PalbreedingBridge"));
        Assert.Equal(ModInstallState.Outdated, service.GetState(_game));
    }

    [Fact]
    public void Install_CopiesTheWholeFolderIncludingEnabledTxt_ToTheUe4ssModsFolder()
    {
        Directory.CreateDirectory(Path.Combine(_game, "Mods", "NativeMods", "UE4SS", "Mods"));
        var service = NewService();

        var target = service.Install(_game);

        Assert.EndsWith(Path.Combine("Mods", "NativeMods", "UE4SS", "Mods", "PaldiumBridge"), target);
        Assert.True(File.Exists(Path.Combine(target, "Scripts", "main.lua")));
        Assert.True(File.Exists(Path.Combine(target, "enabled.txt")));
        Assert.True(File.Exists(Path.Combine(target, "Info.json")));
        Assert.True(service.IsInstalled(_game));
        Assert.False(Directory.Exists(Path.Combine(_game, "Mods", "ManagedMods")));

        // Installing again overwrites.
        File.WriteAllText(Path.Combine(_source, "Scripts", "main.lua"), "print('y')");
        service.Install(_game);
        Assert.Equal("print('y')", File.ReadAllText(Path.Combine(target, "Scripts", "main.lua")));
    }

    [Fact]
    public void Install_WithoutUe4ss_Fails_AndWritesNothing()
    {
        var ex = Assert.Throws<ModInstallException>(() => NewService().Install(_game));
        Assert.Equal(ModInstallException.NoUe4ss, ex.Key);
        Assert.False(Directory.Exists(Path.Combine(_game, "Mods")));
    }

    [Fact]
    public void Install_IntoANonGameFolder_Fails()
    {
        var ex = Assert.Throws<ModInstallException>(() => NewService().Install(Path.Combine(_dir, "empty")));
        Assert.Equal(ModInstallException.NoGame, ex.Key);
    }

    [Fact]
    public void GameRunning_IsReportedFromTheProbe()
    {
        var service = NewService();
        Assert.False(service.IsGameRunning());
        _running = true;
        Assert.True(service.IsGameRunning());
    }

    [Fact]
    public void GameFolder_IsTheLibraryWithTheSteamManifest_NotAnOldLeftoverCopy()
    {
        // D: is a leftover copy (no manifest) listed first; E: has the manifest and the real install folder name.
        var old = Path.Combine(_dir, "D", "steamapps", "common", "Palworld");
        Directory.CreateDirectory(Path.Combine(old, "Pal", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(old, "Pal", "Binaries", "Win64", "Palworld-Win64-Shipping.exe"), "");
        var real = Path.Combine(_dir, "E", "steamapps", "common", "PalworldGame");
        Directory.CreateDirectory(Path.Combine(real, "Pal"));
        File.WriteAllText(Path.Combine(_dir, "E", "steamapps", "appmanifest_1623730.acf"), "\"AppState\"\n{\n\t\"installdir\"\t\t\"PalworldGame\"\n}");

        var service = new ModInstallService(_source, () => [Path.Combine(_dir, "D"), Path.Combine(_dir, "E")], () => false);

        Assert.Equal(real, service.FindGameFolder(null));
        // Without any manifest the library that has the game executable is accepted.
        File.Delete(Path.Combine(_dir, "E", "steamapps", "appmanifest_1623730.acf"));
        Assert.Equal(old, service.FindGameFolder(null));
        // A folder the user picked by hand always wins.
        Assert.Equal(_game, service.FindGameFolder(_game));
    }

    [Fact]
    public void State_IsNotInstalled_Current_OrOutdated()
    {
        Directory.CreateDirectory(Path.Combine(_game, "Mods", "NativeMods", "UE4SS", "Mods"));
        var service = NewService();
        Assert.Equal(ModInstallState.NotInstalled, service.GetState(_game));

        service.Install(_game);
        Assert.Equal(ModInstallState.Current, service.GetState(_game));

        // Extra files of the installed folder (the bridge output) do not matter.
        File.WriteAllText(Path.Combine(ModInstallService.TargetFolder(_game), "extra.txt"), "x");
        Assert.Equal(ModInstallState.Current, service.GetState(_game));

        // A changed shipped script (the app was rebuilt) makes the installed copy outdated until it is installed again.
        File.WriteAllText(Path.Combine(_source, "Scripts", "main.lua"), "print('new')");
        Assert.Equal(ModInstallState.Outdated, service.GetState(_game));
        service.Install(_game);
        Assert.Equal(ModInstallState.Current, service.GetState(_game));

        // The same length but different content is still different; a missing shipped file in the target too.
        File.WriteAllText(Path.Combine(ModInstallService.TargetFolder(_game), "Scripts", "main.lua"), "print('NEW')");
        Assert.Equal(ModInstallState.Outdated, service.GetState(_game));
        service.Install(_game);
        File.Delete(Path.Combine(ModInstallService.TargetFolder(_game), "Info.json"));
        Assert.Equal(ModInstallState.Outdated, service.GetState(_game));
    }

    [Fact]
    public void Install_ReadsTheCopyAgain_AndFailsWhenItDoesNotMatch()
    {
        Directory.CreateDirectory(Path.Combine(_game, "Mods", "NativeMods", "UE4SS", "Mods"));
        // A shipped file that is a directory in the target cannot be copied as a file: the copy fails and is reported.
        Directory.CreateDirectory(Path.Combine(ModInstallService.TargetFolder(_game), "Info.json"));
        var ex = Assert.Throws<ModInstallException>(() => NewService().Install(_game));
        Assert.True(ex.Key is ModInstallException.CopyFailed or ModInstallException.VerifyFailed);
    }

    [Fact]
    public void Install_ParsesModsJsonWithCommentsAndTrailingCommas_AndDisablesNewModIfLegacyRemovalFails()
    {
        var mods = Path.Combine(_game, "Mods", "NativeMods", "UE4SS", "Mods");
        var old = Path.Combine(mods, "PalbreedingBridge");
        Directory.CreateDirectory(Path.Combine(old, "Scripts"));
        File.WriteAllText(Path.Combine(old, "Scripts", "main.lua"), "old");
        File.WriteAllText(Path.Combine(mods, "mods.json"), """
            [
              // comment in UE4SS mods.json
              { "mod_name": 42, "mod_enabled": true },
              { "mod_name": "PalbreedingBridge", "mod_enabled": true, },
            ]
            """);

        // Lock a file in legacy folder so removal fails -> new mod must not keep enabled.txt active.
        using (var locked = new FileStream(Path.Combine(old, "Scripts", "main.lua"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<ModInstallException>(() => NewService().Install(_game));
            Assert.Equal(ModInstallException.OldRemoveFailed, ex.Key);
            Assert.False(File.Exists(Path.Combine(mods, "PaldiumBridge", "enabled.txt")));
        }

        // Once unlocked, install completes cleanly and cleans commented mods.json.
        NewService().Install(_game);
        Assert.True(File.Exists(Path.Combine(mods, "PaldiumBridge", "enabled.txt")));
        Assert.DoesNotContain("PalbreedingBridge", File.ReadAllText(Path.Combine(mods, "mods.json")));
    }
}
