using Paldium.App.Pages.GuidesParts;

namespace Paldium.App.Tests;

public class RanchModelTests
{
    private static readonly IReadOnlyList<Paldium.Core.Domain.PalSpecies> Ranch = RanchModel.Pals(PlannerStateServiceTests.GameDataForTests.Pals);

    [Fact]
    public void Counts_ComeFromTheData()
    {
        Assert.Equal(28, Ranch.Count);
        Assert.Equal(30, RanchModel.Items(Ranch, "en").Count);
        Assert.True(Ranch.Zip(Ranch.Skip(1)).All(p => p.First.DexNo <= p.Second.DexNo));
    }

    [Fact]
    public void Vixy_AtLevel10_HasTheDocumentedChances()
    {
        var vixy = Ranch.Single(p => p.Id == "CuteFox");
        Assert.Equal(7, RanchModel.ItemsOf(vixy).Count);
        var mega = RanchModel.DropAt(vixy, 10, "PalSphere_Mega")!;
        Assert.Equal(0.321, mega.Chance, 3);
        Assert.Equal((2, 3), (mega.Min, mega.Max));
        Assert.Equal(3, RanchModel.FirstLevel(vixy, "PalSphere_Mega"));
        Assert.Null(RanchModel.DropAt(vixy, 1, "PalSphere_Mega"));
        Assert.Equal(4, vixy.Ranch.Single(r => r.Rank == 1).Drops.Count);
    }

    [Fact]
    public void Lamball_DropsWoolEveryLevel_AndTheAmountGrows()
    {
        var lamball = Ranch.Single(p => p.Id == "SheepBall");
        Assert.True(RanchModel.SingleItem(lamball));
        Assert.Equal((1, 1), (RanchModel.DropAt(lamball, 1, "Wool")!.Min, RanchModel.DropAt(lamball, 1, "Wool")!.Max));
        Assert.Equal((3, 10), (RanchModel.DropAt(lamball, 10, "Wool")!.Min, RanchModel.DropAt(lamball, 10, "Wool")!.Max));
    }

    [Fact]
    public void Average_IsChanceTimesTheMiddleOfTheAmount_AndSearchIgnoresCaseAndTurkishLetters()
    {
        var gold = RanchModel.DropAt(Ranch.Single(p => p.Id == "LazyCatfish_Gold"), 10, "Money")!;
        Assert.Equal(0.2 * (300 + 1000) / 2.0, RanchModel.Average(gold), 6);
        Assert.Contains(RanchModel.SearchPals(Ranch, "YUN", "tr"), p => p.Id == "SheepBall");   // Yün (wool)
        Assert.Contains(RanchModel.SearchPals(Ranch, "lamb", "en"), p => p.Id == "SheepBall");
        Assert.Empty(RanchModel.SearchPals(Ranch, "zzzz", "en"));
        Assert.Single(RanchModel.SearchItems(RanchModel.Items(Ranch, "tr"), "yun", "tr"));
    }
}
