using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Guides;
using Paldium.Core.Probability;

namespace Paldium.Core.Tests;

public class GuideFrontMatterTests
{
    private static readonly string Folder = Path.Combine(TestPaths.RepoRoot, "content", "guides");

    [Fact]
    public void FrontMatter_IsSplitFromTheBody()
    {
        const string text = "---\ngroup: mekanik\nsummary: One sentence: with a colon.\ntools:\n  - { label: \"Calculator · Pair\", href: \"/calculator?mode=cift\" }\n  - { label: \"Planner\", href: \"/planner\" }\nart: power-line\ndated: 2026-11\n---\n# Title\n\nBody.";
        var (meta, body) = GuideFrontMatter.Parse(text);
        Assert.Equal("mekanik", meta.Group);
        Assert.Equal("One sentence: with a colon.", meta.Summary);
        Assert.Equal("power-line", meta.Art);
        Assert.Equal("2026-11", meta.Dated);
        Assert.Equal([new GuideTool("Calculator · Pair", "/calculator?mode=cift"), new GuideTool("Planner", "/planner")], meta.Tools);
        Assert.StartsWith("# Title", body);
    }

    [Fact]
    public void AFileWithoutFrontMatter_KeepsItsBody()
    {
        var (meta, body) = GuideFrontMatter.Parse("# Title\n\nText");
        Assert.Equal(GuideMeta.Empty, meta);
        Assert.Equal("# Title\n\nText", body);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(200, 1)]
    [InlineData(201, 2)]
    [InlineData(600, 3)]
    public void TheReadingTime_IsWordsOver200_RoundedUp(int words, int minutes)
    {
        Assert.Equal(minutes, GuideFrontMatter.ReadingMinutes(string.Join(' ', Enumerable.Repeat("word", words))));
    }

    [Fact]
    public void Directives_ReadTheirNameAndArguments()
    {
        var found = GuideDirectives.Find("a [[figure:power-line a=Alpaca b=Anubis]] b [[try:calculator mode=cift a=Alpaca label=\"Open this pair\"]] c [[figure:pool]]");
        Assert.Equal(3, found.Count);
        Assert.Equal(("power-line", "Alpaca"), (found[0].Name, found[0].Arg("a")));
        Assert.Equal(GuideDirectiveKind.Try, found[1].Kind);
        Assert.Equal("Open this pair", found[1].Arg("label"));
        Assert.Equal("calculator?mode=cift&a=Alpaca", found[1].TryHref());
        Assert.Empty(found[2].Args);
    }

    [Fact]
    public void ThePowerLine_OfMelpacaAndAnubis_MatchesTheMockup()
    {
        var rules = BreedingRules.Load(TestPaths.DataFolder);
        var line = PowerLineModel.Build(rules, "Alpaca", "Anubis");
        Assert.Equal(1600, line.Target);
        Assert.Equal("CatVampire", line.ChildId);
        Assert.Equal(1570, line.ChildRank);
        Assert.NotNull(line.NextId);
        Assert.Equal(1650, line.NextRank);
        Assert.Contains(line.Marks, m => !m.IsCandidate);
        Assert.All(line.Marks, m => Assert.InRange(m.Rank, line.AxisMin, line.AxisMax));
    }

    [Fact]
    public void EveryGuide_HasFrontMatter_AToolAndValidDirectives_InBothLanguages()
    {
        var rules = BreedingRules.Load(TestPaths.DataFolder);
        foreach (var file in Directory.GetFiles(Folder, "*.md", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(Folder, file);
            var (meta, body) = GuideFrontMatter.Parse(File.ReadAllText(file));
            Assert.True(meta.Group is "mekanik" or "uygulama" or "meta", $"{name}: group '{meta.Group}'");
            Assert.False(string.IsNullOrWhiteSpace(meta.Summary), $"{name}: summary");
            Assert.NotEmpty(meta.Tools);
            Assert.Contains(meta.Art, new[] { "power-line", "dice", "cake", "eggs", "iv", "import", "sets" });
            Assert.StartsWith("# ", body);
            foreach (var d in GuideDirectives.Find(body).Where(d => d is { Kind: GuideDirectiveKind.Figure, Name: "power-line" }))
                PowerLineModel.Build(rules, d.Arg("a"), d.Arg("b"));
        }
    }

    [Theory]
    [InlineData(1, 0.40)]
    [InlineData(2, 0.24)]
    [InlineData(3, 0.12)]
    [InlineData(4, 0.10)]
    public void TheChanceBarsFigure_NumbersComeFromTheOddsCalculator(int wanted, double expected)
    {
        var calculator = new PassiveOddsCalculator(GameDataLoader.Load(TestPaths.DataFolder));
        var ids = Enumerable.Range(1, wanted).Select(i => "wanted" + i).ToList();
        var result = calculator.Compute(new PassiveOddsQuery(ids, [], ids, 0));
        Assert.Equal(expected, result.ProbabilityPerEgg, 6);
    }
}
