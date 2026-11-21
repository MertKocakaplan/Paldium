using Paldium.App.Pages.PaldexParts;
using Paldium.Core.Owned;

namespace Paldium.App.Tests;

public class CaptureBarModelTests
{
    private static CaptureRecord Record(params (string Id, int Bonus, int Count)[] rows) =>
        new(rows.ToDictionary(r => r.Id, r => r.Bonus), rows.ToDictionary(r => r.Id, r => r.Count));

    [Fact]
    public void ABarAtZero_IsEmpty_AndShowsZeroOfFive()
    {
        var bar = CaptureBarModel.Of(Record(("SheepBall", 0, 0)), "SheepBall");
        Assert.True(bar.None);
        Assert.False(bar.Done);
        Assert.Equal("0/5", bar.Label);
        Assert.Equal(5, bar.Segments);
        Assert.True(CaptureBarModel.Of(Record(), "Unknown").None);   // a species the record does not list
    }

    [Fact]
    public void ABarAtThree_HasThreeLitSegments_AndTheTotalCountsAllCatches()
    {
        var bar = CaptureBarModel.Of(Record(("SheepBall", 3, 3)), "SheepBall");
        Assert.Equal((3, 3, false), (bar.Bonus, bar.Total, bar.Done));
        Assert.Equal("3/5", bar.Label);
        Assert.False(bar.None);
    }

    [Fact]
    public void ABarAtFive_IsDone_ThatTheMimogLookIsDrawnFor_AndKeepsTheTotalAboveFive()
    {
        var bar = CaptureBarModel.Of(Record(("SheepBall", 5, 9)), "SheepBall");
        Assert.True(bar.Done);
        Assert.Equal(9, bar.Total);
        Assert.Equal("5/5", bar.Label);
        Assert.True(CaptureBarModel.Of(Record(("SheepBall", 9, 9)), "SheepBall").Done);   // a bonus above five counts as five
        Assert.Equal("5/5", CaptureBarModel.Of(Record(("SheepBall", 9, 9)), "SheepBall").Label);
    }

    [Fact]
    public void TheSummary_CountsTheFinishedSpecies_OutOfTheOnesThatCanBeCaught()
    {
        var species = PlannerStateServiceTests.GameDataForTests.Pals;
        var capturable = species.Where(Paldium.Core.Queries.PalQuery.CountsForCapture).Take(3).ToList();
        var record = Record((capturable[0].Id, 5, 5), (capturable[1].Id, 4, 4), (capturable[2].Id, 5, 6));

        var (done, total) = CaptureBarModel.Summary(record, species);

        Assert.Equal(2, done);
        Assert.Equal(species.Count(Paldium.Core.Queries.PalQuery.CountsForCapture), total);
    }
}
