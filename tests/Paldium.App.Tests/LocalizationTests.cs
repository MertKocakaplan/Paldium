using System.Xml.Linq;
using Paldium.App.Services;

namespace Paldium.App.Tests;

public class LocalizationTests : IDisposable
{
    private static readonly string ResourceFolder = Path.Combine(FindRepoRoot(), "src", "Paldium.App", "Resources");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pb-loc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static Dictionary<string, string> ReadResx(string fileName) =>
        XDocument.Load(Path.Combine(ResourceFolder, fileName)).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

    private LanguageService NewLanguageService(string language)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Update(s => s with { Language = language });
        return new LanguageService(store);
    }

    [Fact]
    public void EnglishAndTurkishResources_HaveTheSameKeys_AndNoEmptyValues()
    {
        var en = ReadResx("Strings.resx");
        var tr = ReadResx("Strings.tr.resx");

        Assert.Empty(en.Keys.Except(tr.Keys));
        Assert.Empty(tr.Keys.Except(en.Keys));
        Assert.All(en, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), "EN " + kv.Key));
        Assert.All(tr, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), "TR " + kv.Key));
    }

    [Fact]
    public void TurkishResources_KeepBreedingUntranslated()
    {
        var tr = ReadResx("Strings.tr.resx");
        Assert.DoesNotContain(tr, kv => kv.Value.Contains("Çiftleştirme", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Breeding", tr["Term.Breeding"]);
        Assert.Equal("Breeding gücü", tr["Term.BreedingPower"]);
    }

    [Fact]
    public void Localizer_FollowsLanguageServiceImmediately()
    {
        var language = NewLanguageService("en");
        var localizer = new LanguageStringLocalizer(language);

        Assert.Equal("Settings", localizer["Nav.Settings"].Value);
        language.Set("tr");
        Assert.Equal("Ayarlar", localizer["Nav.Settings"].Value);
        language.Set("en");
        Assert.Equal("Settings", localizer["Nav.Settings"].Value);
    }

    [Fact]
    public void Localizer_UnknownKey_ReturnsKeyAndFlagsNotFound()
    {
        var localizer = new LanguageStringLocalizer(NewLanguageService("en"));
        var text = localizer["No.Such.Key"];
        Assert.Equal("No.Such.Key", text.Value);
        Assert.True(text.ResourceNotFound);
    }

    [Fact]
    public void Localizer_FormatsArguments()
    {
        var language = NewLanguageService("tr");
        var localizer = new LanguageStringLocalizer(language);
        Assert.Equal("299 Pal, 115 pasif, 320 aktif yetenek", localizer["Settings.CountsValue", 299, 115, 320].Value);
    }

    [Fact]
    public void LanguageService_NormalizesAndPersistsChoice()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        var language = new LanguageService(store);
        Assert.Equal("en", language.Current);

        var raised = 0;
        language.Changed += (_, _) => raised++;
        language.Set("tr-TR");
        language.Set("tr"); // no change, no event

        Assert.Equal("tr", language.Current);
        Assert.Equal(1, raised);
        Assert.Equal("tr", new SettingsStore(path).Current.Language);
        Assert.Equal("tr", new LanguageService(new SettingsStore(path)).Current);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("de", "en")]
    [InlineData("tr", "tr")]
    [InlineData("TR", "tr")]
    [InlineData("tr-TR", "tr")]
    public void Normalize_FallsBackToEnglish(string? input, string expected)
    {
        Assert.Equal(expected, LanguageService.Normalize(input));
    }

    [Fact]
    public void EnglishResources_HaveNoTurkishSpecificLetters()
    {
        // The English file once received Turkish values for several keys (a wrong language test while adding keys); the panel mixed both languages.
        var turkish = ReadResx("Strings.resx").Where(kv => kv.Value.Any(c => "çğıİöşüÇĞÖŞÜ".Contains(c))).Select(kv => kv.Key).ToList();
        Assert.True(turkish.Count == 0, "Turkish letters in Strings.resx: " + string.Join(", ", turkish));
    }

    [Fact]
    public void KeysAddedForTheMod_AreTranslatedDifferentlyInTurkish()
    {
        // A key whose Turkish text equals the English one is untranslated (the other way round of the test above); a few words are the same in both.
        var en = ReadResx("Strings.resx");
        var tr = ReadResx("Strings.tr.resx");
        var same = en.Where(kv => kv.Key.StartsWith("ModKeys.", StringComparison.Ordinal) || kv.Key.StartsWith("Sources.Scan", StringComparison.Ordinal)
                || kv.Key.StartsWith("Sources.Coverage", StringComparison.Ordinal) || kv.Key.StartsWith("Sources.InstallMod", StringComparison.Ordinal))
            .Where(kv => tr.TryGetValue(kv.Key, out var value) && value == kv.Value).Select(kv => kv.Key).ToList();
        Assert.Empty(same);
    }}
