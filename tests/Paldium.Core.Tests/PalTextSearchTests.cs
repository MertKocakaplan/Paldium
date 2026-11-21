using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class PalTextSearchTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static List<string> Find(string query) =>
        Data.Value.Pals.Where(p => PalTextSearch.Matches(p, query)).Select(p => p.Id).ToList();

    [Theory]
    [InlineData("mozz")]
    [InlineData("MOZZ")]
    [InlineData("zarina")]
    [InlineData("40")]
    [InlineData(" 40 ")]
    [InlineData("CowPal")]
    public void FindsMozzarina(string query)
    {
        Assert.Contains("CowPal", Find(query));
    }

    [Fact]
    public void DexNumber_MatchesExactlyNotPrefix()
    {
        var found = Find("4");
        Assert.DoesNotContain("CowPal", found);
        Assert.All(found, id => Assert.True(Data.Value.Pal(id).DexNo == 4 || Data.Value.Pal(id).Names.Languages
            .Any(l => Data.Value.Pal(id).Names.Get(l).Contains('4')) || id.Contains('4')));
    }

    [Fact]
    public void DexLabel_FindsVariants()
    {
        var found = Find("12B");
        Assert.Contains("PlantSlime_Flower", found);
        Assert.DoesNotContain("PlantSlime", found);
    }

    [Fact]
    public void EmptyQuery_MatchesEverything()
    {
        Assert.Equal(299, Find("").Count);
        Assert.Equal(299, Find("   ").Count);
    }

    [Fact]
    public void TurkishNames_AreSearchable()
    {
        var pal = Data.Value.Pals.First(p => p.Names.TryGet("tr", out var tr) && tr != p.Names.Get("en"));
        var turkish = pal.Names.Get("tr");
        Assert.True(PalTextSearch.Matches(pal, turkish[..Math.Min(4, turkish.Length)]));
    }

    [Fact]
    public void GumossVariants_BothFound_ButDisplayNamesDiffer()
    {
        var found = Find("gumoss");
        Assert.Contains("PlantSlime", found);
        Assert.Contains("PlantSlime_Flower", found);
        Assert.Equal("Gumoss", Data.Value.Pal("PlantSlime").DisplayName("en"));
        Assert.Equal("Gumoss (12B)", Data.Value.Pal("PlantSlime_Flower").DisplayName("en"));
    }

    [Fact]
    public void NoMatch_ReturnsEmpty()
    {
        Assert.Empty(Find("zzzzqqq"));
    }
}
