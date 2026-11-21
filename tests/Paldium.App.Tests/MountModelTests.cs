using Paldium.App.Pages.GuidesParts;
using Paldium.Core.Domain;

namespace Paldium.App.Tests;

public class MountModelTests
{
    private static readonly IReadOnlyList<MountRow> All = MountModel.Rows(PlannerStateServiceTests.GameDataForTests.Pals);
    private static readonly IReadOnlyList<MountPassive> Passives = MountModel.Passives(PlannerStateServiceTests.GameDataForTests.Passives);

    private static int NoOwned(string id) => 0;

    private static MountRow Row(string id) => All.Single(r => r.Pal.Id == id);

    private static MountSelection On(int stars, params string[] ids) => new(stars, ids);

    // The four passives of the spec example: Swift (+30), Runner (+20), Nimble (+10), Legend (+20).
    private static readonly string[] FourMovePassives = ["MoveSpeed_up_3", "MoveSpeed_up_2", "MoveSpeed_up_1", "Legend"];

    [Fact]
    public void Passives_AreTheSpeedAndStaminaOnesOfTheGameData_InTheOrderMovementSwimStamina()
    {
        Assert.Equal(
            ["MoveSpeed_up_1", "Legend", "MoveSpeed_up_2", "MoveSpeed_up_3", "WorldTree_MoveSpeed", "SwimSpeed_up_1", "SwimSpeed_up_2", "SwimSpeed_up_3", "Stamina_Up_2", "Stamina_Up_1", "Stamina_Up_3"],
            Passives.Select(p => p.Skill.Id).ToArray());
        Assert.Equal([10, 20, 20, 30, 50, 30, 40, 50, 25, 50, 75], Passives.Select(p => p.Percent).ToArray());
        Assert.DoesNotContain(Passives, p => p.Skill.Id == "Stamina_Down_1");   // a drawback, not a bonus
    }

    [Fact]
    public void Rows_AreTheMountsOfTheBundle()
    {
        Assert.Equal(121, All.Count);
        Assert.All(All, r => Assert.NotEmpty(r.Mount.Kinds));
        Assert.Equal(6, All.Count(r => r.Pal.Group == "terraria"));
    }

    [Fact]
    public void NoPassivesAndNoStars_GiveTheBundleValues()
    {
        var garm = MountModel.Compute(Row("Garm"), MountSelection.None, Passives);   // Direhowl
        Assert.Equal((800, 1050, 70), (garm.Run, garm.Sprint, garm.Stamina));
        var jet = MountModel.Compute(Row("JetDragon"), MountSelection.None, Passives);
        Assert.Equal((1700, 3300, 110), (jet.Run, jet.Sprint, jet.Stamina));
        Assert.Null(garm.Swim);   // a Pal that does not swim has no swim numbers
    }

    [Fact]
    public void FourMovementPassives_AddUp_ToEightyPercent()
    {
        var jet = MountModel.Compute(Row("JetDragon"), On(0, FourMovePassives), Passives);
        Assert.Equal(5940, jet.Sprint);   // 3300 x 1.8
        Assert.Equal(3060, jet.Run);      // 1700 x 1.8
        Assert.Equal(80, jet.MovePercent);
    }

    [Fact]
    public void Stars_ChangeTheMountsWithAPartnerBonus_AndLeaveTheOthersAlone()
    {
        Assert.Equal((880, 1155), (Run(Row("Garm"), 1), Sprint(Row("Garm"), 1)));
        Assert.Equal((960, 1260), (Run(Row("Garm"), 4), Sprint(Row("Garm"), 4)));
        Assert.Equal(Sprint(Row("JetDragon"), 0), Sprint(Row("JetDragon"), 4));

        // The star bonus and a passive add up: Direhowl at 4 stars with Swift is +50%.
        Assert.Equal(1575, MountModel.Compute(Row("Garm"), On(4, "MoveSpeed_up_3"), Passives).Sprint);

        static int Run(MountRow row, int stars) => MountModel.Compute(row, On(stars), Passives).Run;
        static int Sprint(MountRow row, int stars) => MountModel.Compute(row, On(stars), Passives).Sprint;
    }

    [Fact]
    public void SwimPassives_ChangeSwimOnly_AndMovementPassivesNeverChangeSwim()
    {
        var serpent = Row("Serpent");   // Surfent, a water mount
        var plain = MountModel.Compute(serpent, MountSelection.None, Passives);
        Assert.Equal((900, 1440), (plain.Swim, plain.SwimSprint));
        var swimming = MountModel.Compute(serpent, On(0, "SwimSpeed_up_1"), Passives);
        Assert.Equal((1170, 1872), (swimming.Swim, swimming.SwimSprint));
        Assert.Equal(plain.Run, swimming.Run);
        var running = MountModel.Compute(serpent, On(0, "MoveSpeed_up_3"), Passives);
        Assert.Equal(plain.Swim, running.Swim);

        // Stamina: Fit as a Fiddle +25% (70 x 1.25 = 87.5, rounded up).
        Assert.Equal(88, MountModel.Compute(Row("Garm"), On(0, "Stamina_Up_2"), Passives).Stamina);
    }

    [Fact]
    public void AFifthPassive_IsRefused_AndSwitchingOneOffFreesAPlace()
    {
        var max = PlannerStateServiceTests.GameDataForTests.Mechanics.MaxPassives;
        Assert.Equal(4, max);
        var four = On(0, FourMovePassives);
        Assert.False(MountModel.CanAdd(four, max));
        Assert.Equal(four.PassiveIds, MountModel.Toggle(four, "SwimSpeed_up_1", max).PassiveIds);   // refused

        var three = MountModel.Toggle(four, "Legend", max);
        Assert.Equal(3, three.PassiveIds.Count);
        Assert.True(MountModel.CanAdd(three, max));
        Assert.Contains("SwimSpeed_up_1", MountModel.Toggle(three, "SwimSpeed_up_1", max).PassiveIds);
    }

    private static int? Value(MountRow r, string key) => key switch
    {
        MountModel.SortSaddle => r.Mount.SaddleLevel,
        MountModel.SortSwim => MountModel.Compute(r, MountSelection.None, Passives).Swim,
        MountModel.SortSwimSprint => MountModel.Compute(r, MountSelection.None, Passives).SwimSprint,
        MountModel.SortWalk => r.Pal.Stats.WalkSpeed,
        MountModel.SortRun => r.Pal.Stats.RunSpeed,
        MountModel.SortStamina => r.Pal.Stats.Stamina,
        _ => r.Pal.Stats.RideSprintSpeed,
    };

    [Fact]
    public void DefaultSort_IsSprintHighFirst_AndEveryColumnSortsBothWays()
    {
        var sorted = MountModel.Sort(All, MountModel.SortSprint, true, "en", NoOwned, MountSelection.None, Passives);
        Assert.Equal("JetDragon", sorted[0].Pal.Id);
        Assert.True(sorted.Zip(sorted.Skip(1)).All(p => p.First.Pal.Stats.RideSprintSpeed >= p.Second.Pal.Stats.RideSprintSpeed));

        foreach (var key in new[] { MountModel.SortWalk, MountModel.SortRun, MountModel.SortSprint, MountModel.SortStamina, MountModel.SortSaddle, MountModel.SortSwim, MountModel.SortSwimSprint })
        {
            foreach (var descending in new[] { true, false })
            {
                var list = MountModel.Sort(All, key, descending, "en", NoOwned, MountSelection.None, Passives);
                Assert.Equal(All.Count, list.Count);
                // A row without a value in the column ("-") stays below every row that has one.
                var values = list.Select(r => Value(r, key)).ToList();
                var firstMissing = values.FindIndex(v => v == null);
                if (firstMissing >= 0) Assert.All(values.Skip(firstMissing), v => Assert.Null(v));
                var present = values.Where(v => v != null).Select(v => v!.Value).ToList();
                Assert.Equal(descending ? present.OrderByDescending(x => x).ToList() : present.OrderBy(x => x).ToList(), present);
            }
        }
    }

    [Fact]
    public void Sorting_FollowsTheComputedValues_OfTheSelection()
    {
        // A swim passive grows every swimmer by the same share, so the swimmers and their order do not change; non-swimmers stay at the end.
        var plain = MountModel.Sort(All, MountModel.SortSwim, true, "en", NoOwned, MountSelection.None, Passives);
        var boosted = MountModel.Sort(All, MountModel.SortSwim, true, "en", NoOwned, On(0, "SwimSpeed_up_3"), Passives);
        Assert.Equal(19, plain.Count(r => r.Mount.Is(MountKind.Water)));
        Assert.Equal(plain.Take(19).Select(r => r.Pal.Id).ToList(), boosted.Take(19).Select(r => r.Pal.Id).ToList());
        Assert.All(plain.Take(19), r => Assert.True(r.Mount.Is(MountKind.Water)));
    }

    [Fact]
    public void Filters_Kinds_Search_AndOnlyOwned()
    {
        var flying = MountModel.Filter(All, "", "en", [MountKind.Flying], false, NoOwned);
        Assert.Equal(29, flying.Count);
        Assert.All(flying, r => Assert.True(r.Mount.Is(MountKind.Flying)));

        // One kind is enough: ground or water together are the union.
        var groundOrWater = MountModel.Filter(All, "", "en", [MountKind.Ground, MountKind.Water], false, NoOwned);
        Assert.Equal(All.Count(r => r.Mount.Is(MountKind.Ground) || r.Mount.Is(MountKind.Water)), groundOrWater.Count);

        // Letter case does not matter.
        var found = MountModel.Filter(All, "  JETRAGON ", "en", [], false, NoOwned);
        Assert.Contains(found, r => r.Pal.Id == "JetDragon");

        var owned = MountModel.Filter(All, "", "en", [], true, id => id == "Garm" ? 2 : 0);
        Assert.Equal(["Garm"], owned.Select(r => r.Pal.Id).ToArray());
        Assert.Empty(MountModel.Filter(All, "", "en", [], true, NoOwned));
    }

    [Fact]
    public void StarBonusTips_ListTheUnconditionalBonuses_AndConditionalOnesAreToldApart()
    {
        Assert.Equal([(1, 10), (2, 12), (3, 15), (4, 20)], MountModel.StarBonuses(Row("Garm").Mount)!.ToArray());
        Assert.Null(MountModel.StarBonuses(Row("JetDragon").Mount));
        var anglerfish = Row("GhostAnglerfish");   // its bonus needs a condition
        Assert.Null(MountModel.StarBonuses(anglerfish.Mount));
        Assert.NotNull(MountModel.ConditionalBonuses(anglerfish.Mount));
        var withStars = MountModel.Compute(anglerfish, On(4), Passives);
        Assert.Equal(anglerfish.Pal.Stats.RunSpeed, withStars.Run);   // not counted: the base run speed
    }
}
