using System.Globalization;
using Paldium.App.Pages.GuidesParts;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.App.Tests;

public class EffigyModelTests
{
    private static readonly IReadOnlyList<RelicInfo> All = EffigyModel.Rows(PlannerStateServiceTests.GameDataForTests.Relics);

    private static RelicInfo Relic(string type) => All.Single(r => r.Type == type);

    [Fact]
    public void Rows_Are13Types_InTheGamesOrder_WithTheRankCountsAndTotalsOfTheTable()
    {
        Assert.Equal(13, All.Count);
        Assert.Equal("CapturePower", All[0].Type);   // Lifmunk first, Mimog last
        Assert.Equal("MoveSpeed", All[^1].Type);
        Assert.Equal((92, 287), (Relic("MoveSpeed").Ranks.Count, Relic("MoveSpeed").Total));
        Assert.Equal((15, 100), (Relic("CapturePower").Ranks.Count, Relic("CapturePower").Total));
        Assert.Equal((4, 4), (Relic("SphereHoming").Ranks.Count, Relic("SphereHoming").Total));
        Assert.Equal((20, 20), (Relic("HungerReduction").Ranks.Count, Relic("HungerReduction").Total));
    }

    [Fact]
    public void Ticks_SitAtTheCumulativeRequirements_AndTheLastRankIsTheBarsEnd()
    {
        var lunaris = Relic("SphereHoming");   // 4 ranks of 1 effigy
        Assert.Equal([0.25, 0.5, 0.75], EffigyModel.Ticks(lunaris).Select(t => t.Position).ToArray());
        Assert.Equal([1, 2, 3], EffigyModel.Ticks(lunaris).Select(t => t.Rank).ToArray());

        var lifmunk = Relic("CapturePower");   // 15 ranks, 100 effigies: costs grow, so every rank keeps its line
        var ticks = EffigyModel.Ticks(lifmunk);
        Assert.Equal(14, ticks.Count);
        Assert.Equal(EffigyModel.Cumulative(lifmunk).Take(14).Select(c => c / 100.0).ToArray(), ticks.Select(t => t.Position).ToArray());
        Assert.Equal(100, EffigyModel.Cumulative(lifmunk)[^1]);
    }

    [Fact]
    public void WhenLinesWouldBeCloserThanThreePixels_OnlyEveryFifthRankKeepsOne()
    {
        var mimog = Relic("MoveSpeed");   // 92 ranks on a 287 effigy bar: the first ranks are 1 to 3 effigies = about 1.5 px
        var ticks = EffigyModel.Ticks(mimog);
        Assert.Equal(Enumerable.Range(1, 18).Select(n => n * 5).ToArray(), ticks.Select(t => t.Rank).ToArray());
        Assert.All(ticks, t => Assert.InRange(t.Position, 0.0, 1.0));

        // The same type on a bar wide enough for every line keeps all of them.
        Assert.Equal(91, EffigyModel.Ticks(mimog, barWidth: 4000).Count);
    }

    [Fact]
    public void MaxEffect_IsTheLastRanksEffect_AndNullWhereTheGameTableHasNone()
    {
        Assert.Equal(100, EffigyModel.MaxEffect(Relic("SwimSpeed")));
        Assert.Equal(50, EffigyModel.MaxEffect(Relic("MoveSpeed")));
        Assert.Equal(4, EffigyModel.MaxEffect(Relic("RainbowPassiveRate")));
        Assert.Null(EffigyModel.MaxEffect(Relic("CapturePower")));   // the capture power value is not in this table
        Assert.Equal("17.5", EffigyModel.Percent(17.5, CultureInfo.InvariantCulture));
        Assert.Equal("17,5", EffigyModel.Percent(17.5, CultureInfo.GetCultureInfo("tr")));
        Assert.Equal("50", EffigyModel.Percent(50, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EveryType_HasItsTextsAndEffigyItem_InBothLanguages()
    {
        Assert.All(All, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Names.Get("en")));
            Assert.False(string.IsNullOrWhiteSpace(r.Names.Get("tr")));
            Assert.False(string.IsNullOrWhiteSpace(r.Descriptions.Get("en")));
            Assert.EndsWith("Effigy", r.ItemNames.Get("en"), StringComparison.Ordinal);
            Assert.NotNull(r.IconPath);
        });
    }

    // ---- the player's progress (T133) ----

    [Fact]
    public void Progress_LifmunkWith66Collected2InHand_IsRank11_WithTheCostsOfTheRanksAfterIt()
    {
        var lifmunk = Relic("CapturePower");   // the PM's player 1: spent 64 = the cumulative cost of rank 11
        var progress = EffigyModel.Progress(lifmunk, new RelicCount(2, 66), 0)!;

        Assert.Equal(11, progress.Level);
        Assert.False(progress.AtLeast);
        Assert.False(progress.Maxed);
        var cumulative = EffigyModel.Cumulative(lifmunk);
        Assert.Equal(64, cumulative[10]);
        Assert.Equal(cumulative[11] - 66, progress.NextNeed);
        Assert.Equal(100 - 66, progress.MaxNeed);
        Assert.Equal(0.66, progress.Filled, 3);
    }

    [Fact]
    public void Progress_ATypeAtItsHighestRank_IsMaxed_AndTheSurplusStaysInHand()
    {
        var progress = EffigyModel.Progress(Relic("HungerReduction"), new RelicCount(6, 26), 0)!;   // 26 - 6 = 20 spent

        Assert.Equal(20, progress.Level);
        Assert.True(progress.Maxed);
        Assert.Null(progress.NextNeed);
        Assert.Equal(0, progress.MaxNeed);
        Assert.Equal(1.0, progress.Filled);
        Assert.False(progress.CanRankUp);
    }

    [Fact]
    public void Progress_ASpentCountThatIsNotARankCost_GivesTheRankItReaches_MarkedAtLeast()
    {
        var progress = EffigyModel.Progress(Relic("CapturePower"), new RelicCount(0, 65), 0)!;   // the cumulative costs are 1, 3, 6, 10 ... 64: 65 is between two

        Assert.Equal(11, progress.Level);
        Assert.True(progress.AtLeast);
    }

    [Fact]
    public void Progress_WithEnoughInHandForTheNextRank_SaysSo()
    {
        var lunaris = Relic("SphereHoming");   // 4 ranks of 1 effigy
        var progress = EffigyModel.Progress(lunaris, new RelicCount(2, 3), 0)!;   // spent 1: rank 1; 2 in hand cover rank 2

        Assert.Equal(1, progress.Level);
        Assert.Equal(0, progress.NextNeed);
        Assert.True(progress.CanRankUp);
        Assert.Equal(1, progress.MaxNeed);
    }

    [Fact]
    public void Progress_Mimog_CountsTheSpeciesCaughtFiveTimes_AsCollected()
    {
        var progress = EffigyModel.Progress(Relic("MoveSpeed"), new RelicCount(1, null), mimogSpecies: 13)!;   // 12 spent: ranks cost 1, 3, 3 ... (cumulative 1, 4, 7, 10, 13)

        Assert.Equal(13, progress.Collected);
        Assert.Equal(4, progress.Level);   // spent 12: the cumulative costs 1, 4, 7, 10 fit, 13 does not
        Assert.True(progress.AtLeast);     // 12 is not a rank cost: a count is short or the effigies were not all spent yet
        Assert.Equal(13 / 287.0, progress.Filled, 4);
    }

    [Fact]
    public void Progress_WithOnlyOneOfTheCounts_ShowsThatCount_AndNoRank()
    {
        var onlyHave = EffigyModel.Progress(Relic("JumpPower"), new RelicCount(3, null), 0)!;
        Assert.Equal(3, onlyHave.Have);
        Assert.Null(onlyHave.Level);
        Assert.Null(onlyHave.NextNeed);
        Assert.Equal(0, onlyHave.Filled);

        var onlyCollected = EffigyModel.Progress(Relic("JumpPower"), new RelicCount(null, 7), 0)!;
        Assert.Equal(7, onlyCollected.Collected);
        Assert.Null(onlyCollected.Level);
        Assert.Equal(13, onlyCollected.MaxNeed);
        Assert.Equal(0.35, onlyCollected.Filled, 3);
    }

    [Fact]
    public void Progress_WithoutData_IsNull_AndCountForFillsTheTypesTheSourceLeavesOut()
    {
        Assert.Null(EffigyModel.Progress(Relic("JumpPower"), null, 0));
        Assert.Null(EffigyModel.CountFor(null, "JumpPower"));
        Assert.Null(EffigyModel.CountFor(CaptureRecord.Empty, "JumpPower"));

        var record = new CaptureRecord(new Dictionary<string, int>(), new Dictionary<string, int>()) { Relics = new Dictionary<string, RelicCount> { ["CapturePower"] = new(2, 66) } };
        Assert.Equal(new RelicCount(2, 66), EffigyModel.CountFor(record, "CapturePower"));
        Assert.Equal(new RelicCount(0, 0), EffigyModel.CountFor(record, "JumpPower"));   // the save lists only the types the player has
    }

    [Fact]
    public void EffectAt_IsTheEffectOfARank_ZeroBeforeTheFirst_AndNullWhereTheTableHasNone()
    {
        Assert.Equal(0, EffigyModel.EffectAt(Relic("SwimSpeed"), 0));
        Assert.Equal(5, EffigyModel.EffectAt(Relic("SwimSpeed"), 1));
        Assert.Equal(100, EffigyModel.EffectAt(Relic("SwimSpeed"), 20));
        Assert.Null(EffigyModel.EffectAt(Relic("CapturePower"), 5));
    }
}
