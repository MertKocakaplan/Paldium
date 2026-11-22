using Microsoft.Extensions.Logging.Abstractions;
using Paldium.App.Services;
using Paldium.Core.Data;
using Paldium.Core.Guides;

namespace Paldium.App.Tests;

public class GuideServiceTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static readonly Lazy<GuideService> Service = new(() =>
        new GuideService(GameDataLoader.Load(Path.Combine(Root, "data")), Path.Combine(Root, "content", "guides"), NullLogger<GuideService>.Instance));

    [Fact]
    public void TheLibrary_ListsSevenGuides_InThreeGroups_WithAReadingTime()
    {
        var guides = Service.Value.List("tr");
        Assert.Equal(7, guides.Count);
        Assert.Equal(["mekanik", "uygulama", "meta"], guides.Select(g => g.Group).Distinct().ToList());
        Assert.All(guides, g => Assert.True(g.Minutes >= 1 && g.Tools.Count > 0 && g.Summary.Length > 0));
    }

    [Fact]
    public void TheBreedingGuide_HasAnOutline_AFigureAndATryButton()
    {
        var doc = Service.Value.Load("01-how-breeding-works", "tr")!;
        Assert.Contains(doc.Outline, h => h.Id == "ornek-melpaca-anubis");
        Assert.Contains(doc.Blocks, b => b is GuideDirectiveBlock { Directive: { Kind: GuideDirectiveKind.Figure, Name: "power-line" } });
        Assert.Contains(doc.Blocks, b => b is GuideDirectiveBlock { Directive.Kind: GuideDirectiveKind.Try });
        Assert.Contains("JetDragon", doc.PalIds);
        var html = string.Concat(doc.Blocks.OfType<GuideHtmlBlock>().Select(b => b.Html));
        Assert.Contains("class=\"lede\"", html);
        Assert.Contains("class=\"steps2\"", html);
        Assert.Contains("class=\"palref\"", html);
        Assert.DoesNotContain("<h1", html);
        Assert.False(doc.FellBack);
    }

    [Fact]
    public void TheInheritanceGuide_ShowsThreeFigures()
    {
        var doc = Service.Value.Load("02-passive-inheritance", "en")!;
        var names = doc.Blocks.OfType<GuideDirectiveBlock>().Where(b => b.Directive.Kind == GuideDirectiveKind.Figure).Select(b => b.Directive.Name).ToList();
        Assert.Equal(["pool", "inherit-bars", "chance-bars"], names);
    }

    [Fact]
    public void TheSearch_FindsGuidesByTheirText_IgnoringAccents()
    {
        var found = Service.Value.Search("tr", "yumurta");
        Assert.NotEmpty(found);
        Assert.Contains(Service.Value.Search("tr", "KEKLER"), g => g.Slug == "03-cakes");
        Assert.Empty(Service.Value.Search("tr", "zzzzqqq"));
        Assert.Equal(7, Service.Value.Search("tr", "  ").Count);
    }

    [Fact]
    public void AnUnknownGuide_IsNull()
    {
        Assert.Null(Service.Value.Load("99-nope", "tr"));
    }

    [Fact]
    public void TheReadingMode_Survives_AndRaisesChangedOnce()
    {
        var state = new GuideStateService();
        var raised = 0;
        state.Changed += (_, _) => raised++;
        state.Reading = true;
        state.Reading = true;
        Assert.True(state.Reading);
        Assert.Equal(1, raised);
        Assert.Equal("tr", state.EffectiveLang("tr"));
        state.GuideLang = "en";
        Assert.Equal("en", state.EffectiveLang("tr"));
    }

    [Theory]
    [InlineData("agil", true)]
    [InlineData("tasima", true)]
    [InlineData("binekler", true)]
    [InlineData("heykeller", true)]
    [InlineData("03-cakes", false)]
    [InlineData(null, false)]
    public void IsReferenceSlug_IdentifiesOnlyTheFourReferenceRoutes(string? slug, bool expected)
    {
        Assert.Equal(expected, GuideStateService.IsReferenceSlug(slug));
    }
}
