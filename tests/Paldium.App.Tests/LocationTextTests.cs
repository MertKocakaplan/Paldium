using Paldium.App.Components;
using Paldium.App.Services;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using PalLocation = Paldium.Core.Domain.PalLocation;

namespace Paldium.App.Tests;

public class LocationTextTests : IDisposable
{
    private static readonly Lazy<GameData> Data = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return GameDataLoader.Load(Path.Combine(dir!.FullName, "data"));
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-loc-text-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static OwnedPal Pal(PalLocation location, PalSourceKind source) =>
        new("u", null, "CowPal", null, 1, 1, [], null, [], [], null, null, location, source, "s", DateTimeOffset.UnixEpoch, false, false);

    private string Text(string language, OwnedPal pal)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Update(s => s with { Language = language });
        var lang = new LanguageService(store);
        var localizer = new LanguageStringLocalizer(lang);
        return LocationText.Format(pal, new GameTerms(() => Data.Value, lang, localizer), localizer);
    }

    private string ShortText(string language, OwnedPal pal)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Update(s => s with { Language = language });
        var lang = new LanguageService(store);
        var localizer = new LanguageStringLocalizer(lang);
        return LocationText.ShortPlace(pal, new GameTerms(() => Data.Value, lang, localizer), localizer);
    }

    [Fact]
    public void ShortPlace_ProducesCompactShelfLabels()
    {
        var box = Pal(new PalLocation(LocationKind.Palbox, null, 14, null, 2), PalSourceKind.Save);
        Assert.Equal("Box 3", ShortText("en", box));
        Assert.Equal("Kutu 3", ShortText("tr", box));

        var storageNamed = Pal(new PalLocation(LocationKind.DimensionalStorage, "name:Alice", 0, null, 0, "Alice"), PalSourceKind.ModBridge);
        Assert.Equal("Alice 1", ShortText("en", storageNamed));
        Assert.Equal("Alice 1", ShortText("tr", storageNamed));

        var party = Pal(new PalLocation(LocationKind.Party, null, 1, null), PalSourceKind.Save);
        Assert.Equal("Party", ShortText("en", party));
        Assert.Equal("Takım", ShortText("tr", party));
    }

    [Fact]
    public void ASaveAndTheModWriteTheSamePalBoxPageAndSlot()
    {
        // Save: running index 280 = page 9 (0 based 9 -> "10. page"), slot 10 in the page; the mod: page 9, slot 10 in the page.
        var fromSave = Pal(PalLocation.FromRunningIndex(LocationKind.Palbox, "container-guid", 280, null), PalSourceKind.Save);
        var fromMod = Pal(new PalLocation(LocationKind.Palbox, null, 10, null, 9), PalSourceKind.ModBridge);

        Assert.Equal(9, fromSave.Location.Page);
        Assert.Equal(10, fromSave.Location.SlotIndex);
        Assert.Equal(Text("en", fromSave), Text("en", fromMod));
        Assert.Equal("Palbox page 10 (5,2)", Text("en", fromMod));
        Assert.Equal("Pal Kutusu 10. sayfa (5,2)", Text("tr", fromMod));
    }

    [Fact]
    public void APalOnAnExpedition_GetsANote()
    {
        var pal = Pal(new PalLocation(LocationKind.Base, null, null, "b1"), PalSourceKind.Save) with { IsOnExpedition = true };
        Assert.Equal("Base · on an expedition", Text("en", pal));
        Assert.Equal("Üs · seferde", Text("tr", pal));
    }

    [Fact]
    public void AStoragePal_ShowsItsPageAndTheOwnerOfTheBox_AndOtherPlacesTheirSlot()
    {
        var storage = Pal(new PalLocation(LocationKind.DimensionalStorage, "name:Alice", 0, null, 4), PalSourceKind.ModBridge);
        Assert.Equal("Dimensional Pal Storage (Alice) page 5 (1,1)", Text("en", storage));

        var party = Pal(new PalLocation(LocationKind.Party, null, 1, null), PalSourceKind.Save);
        Assert.Equal("Party, slot 2", Text("en", party));
        Assert.Equal("Takım, 2. yuva", Text("tr", party));

        Assert.Equal("Base", Text("en", Pal(new PalLocation(LocationKind.Base, null, null, "b1"), PalSourceKind.Save)));
        Assert.Equal("Base (Home), slot 3", Text("en", Pal(new PalLocation(LocationKind.Base, "b1", 2, "b1", null, "Home"), PalSourceKind.ModBridge)));
        Assert.Equal("Üs (Ev), 3. yuva", Text("tr", Pal(new PalLocation(LocationKind.Base, "b1", 2, "b1", null, "Ev"), PalSourceKind.ModBridge)));
    }

    [Fact]
    public void AStorageSlot_ShowsTheOwnerName_WhenKnown()
    {
        var known = Pal(new PalLocation(LocationKind.DimensionalStorage, "unknown-1", 4, null, 2, "Alice"), PalSourceKind.ModBridge);
        Assert.Equal("Boyutsal Pal Deposu (Alice) 3. sayfa (5,1)", Text("tr", known));
        var unknown = Pal(new PalLocation(LocationKind.DimensionalStorage, "unknown-1", 4, null, 2), PalSourceKind.ModBridge);
        Assert.Contains("Sahibi bilinmiyor", Text("tr", unknown), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASaveStorageSlot_ShowsTheOwnerNameOfThePal()
    {
        // The player who caught the Pal (A) is not the owner of the storage (B): the text names B.
        var pal = Pal(new PalLocation(LocationKind.DimensionalStorage, "B_DPS", 4, null, 2, "B"), PalSourceKind.Save) with { Owner = new PalOwner("a", "A", null, null) };
        Assert.Equal("Boyutsal Pal Deposu (B) 3. sayfa (5,1)", Text("tr", pal));
        // A save that does not know the owner's name shows no owner.
        var unknown = Pal(new PalLocation(LocationKind.DimensionalStorage, "B_DPS", 4, null, 2), PalSourceKind.Save) with { Owner = new PalOwner("a", "A", null, null) };
        Assert.Equal("Boyutsal Pal Deposu 3. sayfa (5,1)", Text("tr", unknown));
    }

    private (string Container, string Page, string Slot) ThreeLineText(string language, OwnedPal pal)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Update(s => s with { Language = language });
        var lang = new LanguageService(store);
        var localizer = new LanguageStringLocalizer(lang);
        return LocationText.ThreeLine(pal, new GameTerms(() => Data.Value, lang, localizer), localizer);
    }

    [Fact]
    public void ThreeLine_SplitsContainerPageAndSlot_InBothLanguages()
    {
        var pal = Pal(new PalLocation(LocationKind.Palbox, null, 14, null, 0), PalSourceKind.Save);
        Assert.Equal(("Pal Kutusu", "1. sayfa", "3. satır, 3. sütun"), ThreeLineText("tr", pal));
        Assert.Equal(("Palbox", "Page 1", "Row 3, col 3"), ThreeLineText("en", pal));
    }
}
