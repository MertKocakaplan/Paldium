using System.Text.Json;
using Paldium.Import.Bridge;

namespace Paldium.Import.Tests;

public class BridgeSettingsTests : IDisposable
{
    private readonly string _dir = TestSupport.NewTempFolder();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Defaults_AreF9ScanF10ProbeF11Clear_SmartMode_AndValid()
    {
        var settings = new BridgeSettings();
        Assert.Equal("F9", settings.ScanKey);
        Assert.Equal("F10", settings.ProbeKey);
        Assert.Equal("F11", settings.ClearKey);
        Assert.Equal("smart", settings.ScanMode);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void File_UsesTheDocumentedCamelCaseFields_AndRoundTrips()
    {
        var settings = new BridgeSettings { ScanKey = "Ctrl+F9", ProbeKey = "F2", ClearKey = "Alt+K", ScanMode = "full" };
        using var document = JsonDocument.Parse(settings.ToJson());
        Assert.Equal("Ctrl+F9", document.RootElement.GetProperty("scanKey").GetString());
        Assert.Equal("F2", document.RootElement.GetProperty("probeKey").GetString());
        Assert.Equal("Alt+K", document.RootElement.GetProperty("clearKey").GetString());
        Assert.Equal("full", document.RootElement.GetProperty("scanMode").GetString());
        Assert.Equal(settings, BridgeSettings.Parse(settings.ToJson()));
    }

    [Fact]
    public void MissingFields_KeepTheirDefaults()
    {
        var settings = BridgeSettings.Parse("""{ "scanKey": "F5" }""");
        Assert.Equal("F5", settings.ScanKey);
        Assert.Equal("F10", settings.ProbeKey);
        Assert.Equal("smart", settings.ScanMode);
    }

    [Theory]
    [InlineData("F9", "F9")]
    [InlineData("f9", "F9")]
    [InlineData("ctrl+f9", "Ctrl+F9")]
    [InlineData("Shift + Ctrl + k", "Ctrl+Shift+K")]
    [InlineData("Control+Alt+A", "Ctrl+Alt+A")]
    public void Shortcuts_AreNormalized(string input, string expected)
    {
        Assert.Equal(expected, BridgeSettings.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("F13")]
    [InlineData("Ctrl+F9+F10")]
    [InlineData("Win+F9")]
    [InlineData("banana")]
    public void InvalidShortcuts_AreRejected(string input)
    {
        Assert.Null(BridgeSettings.Normalize(input));
        Assert.Contains("invalid:scanKey", new BridgeSettings { ScanKey = input }.Validate());
    }

    [Fact]
    public void TwoActionsOnTheSameShortcut_AreADuplicate_ButCtrlF9AndF9AreDifferent()
    {
        Assert.Contains("duplicate", new BridgeSettings { ScanKey = "F9", ProbeKey = "f9" }.Validate());
        Assert.Empty(new BridgeSettings { ScanKey = "F9", ProbeKey = "Ctrl+F9" }.Validate());
    }

    [Fact]
    public void UnknownScanMode_IsInvalid()
    {
        Assert.Contains("invalid:scanMode", new BridgeSettings { ScanMode = "turbo" }.Validate());
    }

    [Fact]
    public void Compose_UsesTheCanonicalModifierOrder()
    {
        Assert.Equal("Ctrl+Alt+F9", BridgeSettings.Compose(["Alt", "Ctrl"], "F9"));
        Assert.Equal("F9", BridgeSettings.Compose([], "F9"));
    }

    [Fact]
    public void Store_SavesAtomically_LoadsDefaultsForMissingOrBrokenFiles_AndRefusesInvalidSettings()
    {
        var store = new BridgeSettingsStore(_dir);
        Assert.Equal(new BridgeSettings(), store.Load()); // no file

        store.Save(new BridgeSettings { ScanKey = "Ctrl+F9" });
        Assert.Equal("Ctrl+F9", store.Load().ScanKey);
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json.tmp")));

        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ \"scanKey\": ");
        Assert.Equal(new BridgeSettings(), store.Load()); // broken file

        Assert.Throws<ArgumentException>(() => store.Save(new BridgeSettings { ScanKey = "nope" }));
    }
}
