using Paldium.App.Services;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.App.Tests;

public class GameTermsTests : IDisposable
{
    private static readonly Lazy<GameData> Data = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return GameDataLoader.Load(Path.Combine(dir!.FullName, "data"));
    });

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-terms-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private (GameTerms Terms, LanguageService Language) Create(string language, bool withData = true)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Update(s => s with { Language = language });
        var lang = new LanguageService(store);
        return (new GameTerms(() => withData ? Data.Value : null, lang, new LanguageStringLocalizer(lang)), lang);
    }

    [Fact]
    public void GameText_IsUsed_InTheSelectedLanguage()
    {
        var (terms, language) = Create("tr");
        Assert.Equal("Pal Kutusu", terms.T("Palbox"));
        Assert.Equal("Sergileme Kafesi", terms.T("ViewingCage"));
        language.Set("en");
        Assert.Equal("Palbox", terms.T("Palbox"));
        Assert.Equal("Viewing Cage", terms.T("ViewingCage"));
    }

    [Fact]
    public void TermsMissingFromTheGame_FallBackToResources_ThenToTheId()
    {
        var (terms, language) = Create("tr");
        Assert.Equal("Breeding", terms.T("Breeding"));
        Assert.Equal("Breeding gücü", terms.T("BreedingPower"));
        Assert.Equal("Zindan", terms.T("Dungeon"));
        Assert.Equal("NoSuchTerm", terms.T("NoSuchTerm"));
        Assert.Null(terms.TryT("NoSuchTerm"));
        language.Set("en");
        Assert.Equal("Breeding", terms.T("Breeding"));
    }

    [Fact]
    public void WithoutGameData_ResourceFallbacksStillWork()
    {
        var (terms, _) = Create("tr", withData: false);
        Assert.Equal("Pal Kutusu", terms.T("Palbox"));
        Assert.Equal("Alfa Pal", terms.T("Alpha"));
    }

    [Fact]
    public void Locations_UseGameNames_AndResourcesForTheRest()
    {
        var (terms, language) = Create("tr");
        Assert.Equal("Pal Kutusu", terms.Location(LocationKind.Palbox));
        Assert.Equal("Sergileme Kafesi", terms.Location(LocationKind.ViewingCage));
        Assert.Equal("Boyutsal Pal Deposu", terms.Location(LocationKind.DimensionalStorage));
        Assert.Equal("Global Pal Kutusu", terms.Location(LocationKind.GlobalStorage));
        Assert.Equal("Takım", terms.Location(LocationKind.Party));
        language.Set("en");
        Assert.Equal("Viewing Cage", terms.Location(LocationKind.ViewingCage));
        Assert.Equal("Party", terms.Location(LocationKind.Party));
    }

    [Fact]
    public void EggName_ShowsTheGamesFullEggName_OrTheSizeWordWhenElementIsUnknown()
    {
        var (terms, language) = Create("tr");
        Assert.Equal("Kocaman Taşsı Yumurta", terms.EggName("Earth", "Huge"));
        Assert.Equal("Büyük Sıradan Yumurta", terms.EggName("Normal", "Large"));
        Assert.Equal("Dev", terms.EggName(null, "Huge"));
        Assert.Equal("Normal", terms.EggName("NoElement", "Normal"));
        language.Set("en");
        Assert.Equal("Huge Rocky Egg", terms.EggName("Earth", "Huge"));
        Assert.Equal("Huge", terms.EggName(null, "Huge"));
    }

    [Fact]
    public void ObtainMethods_UseGameTermsWhereTheGameHasThem()
    {
        var (terms, language) = Create("tr");
        Assert.Equal("Alfa Pal", terms.ObtainMethod("AlphaBoss"));
        Assert.Equal("Zindan", terms.ObtainMethod("Dungeon"));
        Assert.Equal("Akın", terms.ObtainMethod("Raid"));
        Assert.Equal("Vahşi", terms.ObtainMethod("Wild"));
        Assert.Equal("Hapsedilmiş boss", terms.ObtainMethod("Imprisoned"));
        Assert.Equal("Unknown", terms.ObtainMethod("Unknown"));
        language.Set("en");
        Assert.Equal("Alpha Pal", terms.ObtainMethod("AlphaBoss"));
        Assert.Equal("Wild", terms.ObtainMethod("Wild"));
    }

    [Fact]
    public void WorkTypeNames_AreTurkishInTheBundle()
    {
        Assert.Equal(12, Data.Value.WorkTypes.Count);
        Assert.All(Data.Value.WorkTypes, w => Assert.True(w.Names.TryGet("tr", out _), w.Id));
        Assert.Equal("El İşi", Data.Value.WorkTypes.Single(w => w.Id == "Handiwork").Names.Get("tr"));
    }
}
