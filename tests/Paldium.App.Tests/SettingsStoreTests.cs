using System.Text.Json;
using Paldium.App.Services;

namespace Paldium.App.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void MissingFile_YieldsDefaults()
    {
        var store = new SettingsStore(SettingsPath);
        Assert.Equal("en", store.Current.Language);
        Assert.Equal(1, store.Current.SchemaVersion);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void AnOldFileWithTheRemovedThemeSetting_StillLoads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 1, "language": "tr", "theme": "light" }""");
        Assert.Equal("tr", new SettingsStore(SettingsPath).Current.Language);
    }

    [Fact]
    public void Update_PersistsAndReloads()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(s => s with { Language = "tr" });

        Assert.True(File.Exists(SettingsPath));
        var reloaded = new SettingsStore(SettingsPath);
        Assert.Equal("tr", reloaded.Current.Language);
    }

    [Fact]
    public void Update_WritesAtomically_NoTempFileLeftBehind()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(s => s with { Language = "tr" });
        store.Update(s => s with { Planner = s.Planner with { MaxSteps = 4 } });

        Assert.False(File.Exists(SettingsPath + ".tmp"));
        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal("tr", doc.RootElement.GetProperty("language").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("planner").GetProperty("maxSteps").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("theme", out _)); // the unused theme setting is gone
    }

    [Fact]
    public void Update_ReplacesExistingFile_WithoutLosingItOnFailure()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(s => s with { Language = "tr" });
        var before = File.ReadAllText(SettingsPath);

        // A failing change function must not touch the saved file.
        Assert.Throws<InvalidOperationException>(() => store.Update(_ => throw new InvalidOperationException("boom")));

        Assert.Equal(before, File.ReadAllText(SettingsPath));
        Assert.Equal("tr", store.Current.Language);
    }

    [Fact]
    public void CorruptFile_YieldsDefaults_AndIsBackedUp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var store = new SettingsStore(SettingsPath);

        Assert.Equal("en", store.Current.Language);
        Assert.True(File.Exists(SettingsPath + ".bak"));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void Update_RaisesChanged()
    {
        var store = new SettingsStore(SettingsPath);
        AppSettings? seen = null;
        store.Changed += (_, s) => seen = s;

        store.Update(s => s with { Language = "tr" });

        Assert.Equal("tr", seen?.Language);
    }

    [Fact]
    public void PaldexSettings_RoundTrip_WithReadableEnumsAndNoComputedFields()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(s => s with
        {
            Paldex = new PaldexSettings
            {
                View = "cards",
                FiltersOpen = true,
                Filter = new Paldium.Core.Queries.PalFilter
                {
                    Text = "mozz",
                    MinWork = new Dictionary<string, int> { ["Handiwork"] = 3 },
                    Elements = ["Fire"],
                    MinRarity = 2,
                    Nocturnal = true,
                },
                Sort = new Paldium.Core.Queries.PalSort
                {
                    Key = Paldium.Core.Queries.PalSortKey.Work,
                    WorkType = "Mining",
                    Descending = true,
                },
            },
        });

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"key\": \"Work\"", text);
        Assert.DoesNotContain("isEmpty", text);

        var loaded = new SettingsStore(SettingsPath).Current.Paldex;
        Assert.Equal("cards", loaded.View);
        Assert.True(loaded.FiltersOpen);
        Assert.Equal("mozz", loaded.Filter.Text);
        Assert.Equal(3, loaded.Filter.MinWork["Handiwork"]);
        Assert.Equal(["Fire"], loaded.Filter.Elements);
        Assert.Equal(2, loaded.Filter.MinRarity);
        Assert.True(loaded.Filter.Nocturnal);
        Assert.Equal(Paldium.Core.Queries.PalSortKey.Work, loaded.Sort.Key);
        Assert.Equal("Mining", loaded.Sort.WorkType);
        Assert.True(loaded.Sort.Descending);
    }

    [Fact]
    public void OldSettingsFile_WithoutPaldexSection_StillLoads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ \"schemaVersion\": 1, \"language\": \"tr\", \"theme\": \"dark\" }");

        var store = new SettingsStore(SettingsPath);

        Assert.Equal("tr", store.Current.Language);
        Assert.Equal("cards", store.Current.Paldex.View);
        Assert.True(store.Current.Paldex.Filter.IsEmpty);
        Assert.Equal("table", store.Current.MyPals.View);
    }

    [Fact]
    public void PaldexSettings_View_PersistsDenseAndDefaultsToCards()
    {
        var store = new SettingsStore(SettingsPath);
        Assert.Equal("cards", store.Current.Paldex.View);

        store.Update(s => s with { Paldex = s.Paldex with { View = "dense" } });
        Assert.Equal("dense", new SettingsStore(SettingsPath).Current.Paldex.View);
    }

    [Fact]
    public void MyPalsSettings_View_PersistsAndDefaultsToTable()
    {
        var store = new SettingsStore(SettingsPath);
        Assert.Equal("table", store.Current.MyPals.View);

        store.Update(s => s with { MyPals = s.MyPals with { View = "cards" } });
        Assert.Equal("cards", new SettingsStore(SettingsPath).Current.MyPals.View);

        store.Update(s => s with { MyPals = s.MyPals with { View = "dense" } });
        Assert.Equal("dense", new SettingsStore(SettingsPath).Current.MyPals.View);
    }

    [Fact]
    public void UpdateCreatesMissingDirectory()
    {
        var nested = Path.Combine(_dir, "a", "b", "settings.json");
        var store = new SettingsStore(nested);
        store.Update(s => s with { Language = "tr" });
        Assert.True(File.Exists(nested));
    }
}
