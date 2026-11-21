using Paldium.App.Pages.GuidesParts;

namespace Paldium.App.Tests;

public class TransportModelTests
{
    private static readonly IReadOnlyList<TransportRow> All = TransportModel.Rows(PlannerStateServiceTests.GameDataForTests.Pals);

    private static int NoOwned(string id) => 0;

    [Fact]
    public void Rows_AreThePalsWithTheTransportingWork_WithTheNumbersOfTheGameData()
    {
        Assert.Equal(148, All.Count);
        Assert.All(All, r => Assert.InRange(r.Level, 1, TransportModel.MaxLevel));
        Assert.Equal(8, All.Count(r => r.Pal.Group == "terraria"));
        var anubis = All.Single(r => r.Pal.Id == "Anubis");
        Assert.Equal((4, 480, 800), (anubis.Level, anubis.TransportSpeed, anubis.RunSpeed));
        Assert.Equal(150, All.Single(r => r.Pal.Id == "Yeti").TransportSpeed);      // Wumpo
        Assert.Equal(500, All.Single(r => r.Pal.Id == "Mothman").TransportSpeed);   // Silvance
    }

    [Fact]
    public void DefaultSort_IsCarryingSpeedHighFirst_AndTiesAreBrokenSameWayEveryTime()
    {
        var sorted = TransportModel.Sort(All, TransportModel.SortSpeed, true, "en", NoOwned);
        Assert.True(sorted.Zip(sorted.Skip(1)).All(p => p.First.TransportSpeed >= p.Second.TransportSpeed));
        Assert.Equal(600, sorted[0].TransportSpeed);   // Eye of Cthulhu (Terraria), then Silvance and the Faleris at 500
        Assert.Equal("YakushimaBoss001", sorted[0].Pal.Id);
        // The same list from a reversed input: nothing depends on the input order.
        var again = TransportModel.Sort(All.Reverse(), TransportModel.SortSpeed, true, "en", NoOwned);
        Assert.Equal(sorted.Select(r => r.Pal.Id), again.Select(r => r.Pal.Id));
    }

    [Fact]
    public void Sorting_ByLevelPalAndOwned_FollowsTheColumn()
    {
        var byLevel = TransportModel.Sort(All, TransportModel.SortLevel, true, "en", NoOwned);
        Assert.Equal(TransportModel.MaxLevel, byLevel[0].Level);
        Assert.True(byLevel.Zip(byLevel.Skip(1)).All(p => p.First.Level >= p.Second.Level));

        var byPal = TransportModel.Sort(All, TransportModel.SortPal, false, "en", NoOwned);
        Assert.True(byPal.Zip(byPal.Skip(1)).All(p => p.First.Pal.DexNo <= p.Second.Pal.DexNo));
        Assert.False(TransportModel.DefaultDescending(TransportModel.SortPal));
        Assert.True(TransportModel.DefaultDescending(TransportModel.SortRun));

        var anubisOwned = TransportModel.Sort(All, TransportModel.SortOwned, true, "en", id => id == "Anubis" ? 3 : 0);
        Assert.Equal("Anubis", anubisOwned[0].Pal.Id);

        var ascending = TransportModel.Sort(All, TransportModel.SortSpeed, false, "en", NoOwned);
        Assert.Equal(All.Min(r => r.TransportSpeed), ascending[0].TransportSpeed);
    }

    [Fact]
    public void Filters_MinimumLevel_SearchAndOnlyOwned()
    {
        var level7 = TransportModel.Filter(All, "", "en", 7, false, NoOwned);
        Assert.NotEmpty(level7);
        Assert.All(level7, r => Assert.Equal(7, r.Level));

        // Letter case and Turkish characters do not matter.
        var found = TransportModel.Filter(All, "  ANUBİS ", "en", 1, false, NoOwned);
        Assert.Contains(found, r => r.Pal.Id == "Anubis");
        Assert.All(found, r => Assert.Contains("anubis", Paldium.Core.Queries.TextFold.Fold(r.Pal.Names.Get("en")), StringComparison.Ordinal));

        var owned = TransportModel.Filter(All, "", "en", 1, true, id => id == "Anubis" || id == "Yeti" ? 1 : 0);
        Assert.Equal(["Anubis", "Yeti"], owned.Select(r => r.Pal.Id).Order().ToArray());
        Assert.Empty(TransportModel.Filter(All, "", "en", 1, true, NoOwned));
    }
}
