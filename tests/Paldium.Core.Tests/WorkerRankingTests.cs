using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class WorkerRankingTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static readonly Lazy<IReadOnlyList<WorkerColumns>> Columns = new(() => WorkerRanking.Compute(Data.Value));

    private static WorkerColumns Of(string workType) => Columns.Value.Single(c => c.WorkType == workType);

    [Fact]
    public void EveryWorkTypeThatSomeSpeciesHas_GetsThreeColumns()
    {
        Assert.Equal(12, Columns.Value.Count);
        foreach (var column in Columns.Value)
        {
            Assert.NotEmpty(column.Late);
            Assert.InRange(column.Late.Count, 1, WorkerRanking.PerColumn);
            Assert.InRange(column.Mid.Count, 0, WorkerRanking.PerColumn);
            Assert.InRange(column.Early.Count, 0, WorkerRanking.PerColumn);
        }
    }

    [Fact]
    public void StageBounds_AreOnTheEarliestLevel()
    {
        foreach (var column in Columns.Value)
        {
            Assert.All(column.Early, e => Assert.InRange(e.EarliestLevel!.Value, 1, WorkerRanking.EarlyMaxLevel));
            Assert.All(column.Mid, e => Assert.InRange(e.EarliestLevel!.Value, 1, WorkerRanking.MidMaxLevel));
        }
    }

    [Fact]
    public void TheLastStage_HoldsTheBestWorkersOfTheGame()
    {
        // The level 8 workers named by the research (RESEARCH 12.3), the first of each type.
        Assert.Equal("KabukiMan", Of("Kindling").Late[0].Pal.Id);          // Renjishi
        Assert.Equal("MonochromeQueen", Of("Handiwork").Late[0].Pal.Id);  // Solenne
        Assert.Equal("DomeArmorDragon", Of("Mining").Late[0].Pal.Id);     // Aegidron
        Assert.Equal("Mothman", Of("MedicineProduction").Late[0].Pal.Id); // Silvance
        Assert.All(Columns.Value.Where(c => c.WorkType != "Transporting" && c.WorkType != "Farming"), c => Assert.True(c.Late[0].WorkLevel >= 4, c.WorkType));
    }

    [Fact]
    public void ASpeciesThatCanOnlyBeBred_IsInTheLastStageOnly()
    {
        // Dandilord (FlowerPrince) has no wild, boss, dungeon, cage, shop or fishing source: breeding only.
        var dandilord = Data.Value.Pal("FlowerPrince");
        Assert.Null(WorkerRanking.Earliest(dandilord).Level);
        Assert.Contains(Of("Planting").Late, e => e.Pal.Id == "FlowerPrince");
        Assert.DoesNotContain(Of("Planting").Early, e => e.Pal.Id == "FlowerPrince");
        Assert.DoesNotContain(Of("Planting").Mid, e => e.Pal.Id == "FlowerPrince");
    }

    [Fact]
    public void EarliestLevel_IsTheLowestOfTheSourcesAndNamesTheMethod()
    {
        foreach (var pal in Data.Value.Pals.Where(p => p.Obtain != null && !p.IsUncapturable).Take(150))
        {
            var (level, method) = WorkerRanking.Earliest(pal);
            if (level == null) { Assert.Null(method); continue; }
            Assert.NotNull(method);
            if (pal.WildLevel is { Min: > 0 } wild) Assert.True(level <= wild.Min, pal.Id);
        }

        // A species with a wild level: the wild level or lower.
        var lamball = Data.Value.Pal("SheepBall");
        Assert.Equal(lamball.WildLevel!.Min, WorkerRanking.Earliest(lamball).Level);
    }

    [Fact]
    public void Order_IsWorkLevel_ThenEarliestLevel_ThenOtherWork()
    {
        foreach (var column in Columns.Value)
            foreach (var list in new[] { column.Early, column.Mid, column.Late })
                for (var i = 1; i < list.Count; i++)
                {
                    var a = list[i - 1];
                    var b = list[i];
                    Assert.True(a.WorkLevel > b.WorkLevel ||
                                (a.WorkLevel == b.WorkLevel && (a.EarliestLevel ?? int.MaxValue) < (b.EarliestLevel ?? int.MaxValue)) ||
                                (a.WorkLevel == b.WorkLevel && a.EarliestLevel == b.EarliestLevel && a.OtherWork >= b.OtherWork),
                        $"{column.WorkType}: {a.Pal.Id} before {b.Pal.Id}");
                }
    }
}
