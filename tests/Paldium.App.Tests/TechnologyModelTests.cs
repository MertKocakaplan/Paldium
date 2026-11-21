using Paldium.App.Pages.TechnologyParts;
using Paldium.Core.Domain;

namespace Paldium.App.Tests;

public class TechnologyModelTests
{
    private static readonly IReadOnlyList<TechnologyInfo> All = PlannerStateServiceTests.GameDataForTests.Technologies;

    private static TechnologyInfo Tech(string id) => All.Single(t => t.Id == id);

    private static IReadOnlyList<TechLevel> Levels(TechFilter? filter = null, string lang = "en") => TechnologyModel.Levels(All, filter ?? new TechFilter(), lang);

    [Fact]
    public void WithoutFilters_AllEightyLevelsShow_InTheGamesOrder_WithTheirPoints()
    {
        var levels = Levels();
        Assert.Equal(Enumerable.Range(1, 80), levels.Select(l => l.Level));
        Assert.Equal(588, levels.Sum(l => l.Technologies.Count));
        Assert.Equal((5, 5, 0), (levels[0].Technologies.Count, levels[0].Points, levels[0].AncientPoints));
        Assert.Equal(7, levels[1].Technologies.Count);
        Assert.Equal(8, levels[1].Points);
        Assert.Equal(4, levels[79].Technologies.Count);
        Assert.All(levels, l => Assert.Equal(l.Technologies.Select(t => t.Index).Order(), l.Technologies.Select(t => t.Index)));
        // Normal and ancient points are separate sums over the level's technologies.
        Assert.Equal(All.Where(t => t.Level == 17 && t.Ancient).Sum(t => t.Cost), levels[16].AncientPoints);
        Assert.Equal(All.Where(t => t.Level == 17 && !t.Ancient).Sum(t => t.Cost), levels[16].Points);
    }

    [Fact]
    public void AncientFilter_KeepsOnlyTheAncientOnes_AndDropsEmptyLevels()
    {
        var levels = Levels(new TechFilter(AncientOnly: true));
        Assert.Equal(51, levels.Sum(l => l.Technologies.Count));
        Assert.All(levels.SelectMany(l => l.Technologies), t => Assert.True(t.Ancient));
        Assert.True(levels.Count < 80);
        // The level's point totals still count all of its technologies.
        Assert.Equal(Levels().Single(l => l.Level == 17).Points, levels.Single(l => l.Level == 17).Points);
    }

    [Fact]
    public void PrerequisiteFilter_KeepsTechnologiesThatNeedABossAResearchOrAnotherTechnology()
    {
        var shown = Levels(new TechFilter(PrerequisiteOnly: true)).SelectMany(l => l.Technologies).ToList();
        Assert.Equal(All.Count(TechnologyModel.HasPrerequisite), shown.Count);
        Assert.Contains(shown, t => t.Id == "GrapplingGun2");   // another technology
        Assert.Contains(shown, t => t.Id == "Silo");            // a research
        Assert.Contains(shown, t => t.Requires?.TowerBossType == "GrassBoss");
        Assert.DoesNotContain(shown, t => t.Id == "Workbench");
        Assert.False(TechnologyModel.HasPrerequisite(Tech("Workbench")));
    }

    [Fact]
    public void Search_LooksAtTheNameAndAtWhatTheTechnologyUnlocks_IgnoringCase()
    {
        var names = Levels(new TechFilter(Query: "SPHERE")).SelectMany(l => l.Technologies).Select(t => t.Names.Get("en")).ToList();
        Assert.Contains("Pal Sphere", names);
        Assert.Contains("Mega Sphere", names);
        Assert.Contains("Sphere Workbench", names);

        // A building inside a set is found through the set.
        var set = Tech("Wooden_houseset");
        var part = set.Unlocks.Select(u => u.Name.Get("en")).First(n => !set.Names.Get("en").Contains(n, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(set, Levels(new TechFilter(Query: part)).SelectMany(l => l.Technologies));

        // Emptied levels are gone; filters and search combine.
        Assert.True(Levels(new TechFilter(Query: "sphere")).Count < 80);
        Assert.All(Levels(new TechFilter(Query: "sphere", AncientOnly: true)).SelectMany(l => l.Technologies), t => Assert.True(t.Ancient));
        Assert.Empty(Levels(new TechFilter(Query: "zzzzqq")));
    }

    [Fact]
    public void Search_AlsoFindsTheEnglishNameInTheTurkishUi()
    {
        var found = Levels(new TechFilter(Query: "Pal Sphere"), "tr").SelectMany(l => l.Technologies).Select(t => t.Id);
        Assert.Contains("Special_PalSphere_Grade_01", found);
    }

    [Fact]
    public void JumpTarget_IsTheWantedLevel_OrTheNextShownOne()
    {
        Assert.Equal(40, TechnologyModel.JumpTarget(Levels(), 40));
        var ancient = Levels(new TechFilter(AncientOnly: true));
        var target = TechnologyModel.JumpTarget(ancient, 40)!.Value;
        Assert.True(target >= 40);
        Assert.Contains(ancient, l => l.Level == target);
        Assert.Equal(ancient[^1].Level, TechnologyModel.JumpTarget(ancient, 81));
        Assert.Null(TechnologyModel.JumpTarget([], 10));
        Assert.Equal([1, 10, 20, 30, 40, 50, 60, 70, 80], TechnologyModel.JumpLevels);
    }

    [Fact]
    public void LeadsTo_ListsTheTechnologiesThatNeedThisOneFirst()
    {
        var next = TechnologyModel.LeadsTo(All, Tech("GrapplingGun")).Select(t => t.Id).ToList();
        Assert.Equal(["GrapplingGun2"], next);
        Assert.Empty(TechnologyModel.LeadsTo(All, Tech("GrapplingGun5")));
    }

    [Fact]
    public void IconOf_FallsBackToAnUnlockedIcon_AndAlmostEveryTechnologyHasOne()
    {
        Assert.All(All.Where(t => t.IconPath != null), t => Assert.Equal(t.IconPath, TechnologyModel.IconOf(t)));
        Assert.True(All.Count(t => TechnologyModel.IconOf(t) == null) <= 3);
    }
}
