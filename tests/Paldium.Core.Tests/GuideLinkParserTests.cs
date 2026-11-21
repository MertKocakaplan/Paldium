using Paldium.Core.Guides;

namespace Paldium.Core.Tests;

public class GuideLinkParserTests
{
    [Fact]
    public void Find_ReturnsPalAndPassiveLinksInOrder()
    {
        var links = GuideLinkParser.Find("Use [[pal:CowPal]] with [[passive:CraftSpeed_up2]] and [[pal:Anubis]].");
        Assert.Equal([new GuideLink(GuideLinkKind.Pal, "CowPal"), new GuideLink(GuideLinkKind.Passive, "CraftSpeed_up2"), new GuideLink(GuideLinkKind.Pal, "Anubis")], links);
    }

    [Fact]
    public void Replace_UsesTheResolverMarkup()
    {
        var result = GuideLinkParser.Replace("A [[pal:CowPal]] b", l => $"<x {l.Kind}:{l.Id}>");
        Assert.Equal("A <x Pal:CowPal> b", result);
    }

    [Fact]
    public void Replace_UnknownId_BecomesPlainTextAndWarns()
    {
        var warnings = new List<string>();
        var result = GuideLinkParser.Replace("See [[passive:Nope]].", _ => null, warnings.Add);
        Assert.Equal("See Nope.", result);
        Assert.Single(warnings);
        Assert.Contains("Nope", warnings[0]);
    }

    [Theory]
    [InlineData("[pal:CowPal]")]
    [InlineData("[[unknown:CowPal]]")]
    [InlineData("[[pal:]]")]
    [InlineData("[[pal:Cow Pal]]")]
    public void Malformed_LinksAreIgnored(string text)
    {
        Assert.Empty(GuideLinkParser.Find(text));
        Assert.Equal(text, GuideLinkParser.Replace(text, _ => "X"));
    }
}
