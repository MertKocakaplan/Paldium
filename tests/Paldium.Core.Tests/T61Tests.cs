using Paldium.Core.Breeding;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

/// <summary>QA-2 calculation fixes (T61).</summary>
public class T61Tests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));
    private static readonly Lazy<BreedingCalculator> Breeding = new(() => BreedingCalculator.Load(Data.Value, TestPaths.DataFolder));

    private static OwnedPal Pal(string uid, string species, int level, Ivs? ivs, Gender gender = Gender.Male) =>
        new(uid, null, species, gender, level, 1, [], ivs, [], [], null, null, new PalLocation(LocationKind.Palbox, null, null, null),
            PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    [Theory]
    [InlineData("NightLady", 35)]
    [InlineData("NightLady_Dark", 45)]
    [InlineData("KingBahamut_Dragon", 55)]
    [InlineData("DarkMechaDragon", 65)]
    [InlineData("LegendDeer", 70)]
    public void SpeciesThatOnlyComeFromRaids_HaveAnEarliestLevel(string id, int level)
    {
        var (earliest, method) = WorkerRanking.Earliest(Data.Value.Pal(id));
        Assert.Equal(level, earliest);
        Assert.Equal("Raid", method);
    }

    [Fact]
    public void ImprisonedBosses_AreCounted()
    {
        var withImprisoned = Data.Value.Pals.Where(p => p.Obtain is { } o && o.Bosses.Any(b => b.Kind == "Imprisoned")).ToList();
        Assert.NotEmpty(withImprisoned);
        foreach (var pal in withImprisoned.Where(p => !p.IsUncapturable))
        {
            var level = WorkerRanking.Earliest(pal).Level;
            Assert.NotNull(level);
            Assert.True(level <= pal.Obtain!.Bosses.Where(b => b.Kind == "Imprisoned").Min(b => b.Level), pal.Id);
        }
    }

    [Fact]
    public void TheHighestWorkLevelOfTheData_IsAbove6()
    {
        Assert.True(Data.Value.Pals.SelectMany(p => p.Work.Values).Max() >= 8);
    }

    [Fact]
    public void ATargetWithTwoElements_GetsTheDualPassiveThatHoldsBoth()
    {
        // Dark + Ice: Witch (Dark/Ice), not Invader (Dark/Dragon).
        var set = RolePresets.Build(Data.Value, RolePresets.CombatElement, PresetVariant.Best, Data.Value.Pal("BlackPuppy_Ice"))!;
        Assert.Contains("Witch", set.PassiveIds);
        Assert.DoesNotContain("Invader", set.PassiveIds);
    }

    [Fact]
    public void EveryRole_EveryElement_GivesFourEasyPassives()
    {
        foreach (var element in Data.Value.Pals.SelectMany(p => p.Elements).Distinct())
        {
            var target = Data.Value.Pals.First(p => p.Elements.Contains(element));
            foreach (var role in RolePresets.Roles)
            {
                var preset = RolePresets.Build(Data.Value, role, PresetVariant.Easy, target);
                Assert.NotNull(preset);
                Assert.Equal(4, preset!.PassiveIds.Count);
                Assert.Equal(4, preset.PassiveIds.Distinct().Count());
            }
        }
    }

    [Fact]
    public void BestFirst_OrdersByIvTotal_ThenLevel_AndPutsUnknownIvsLast()
    {
        var pals = new[]
        {
            Pal("noIv", "Lamball", 50, null),
            Pal("highLevel", "Lamball", 40, new Ivs(50, 50, 50)),
            Pal("highIv", "Lamball", 10, new Ivs(90, 90, 90)),
            Pal("sameIvHigherLevel", "Lamball", 45, new Ivs(50, 50, 50)),
        };
        Assert.Equal(["highIv", "sameIvHigherLevel", "highLevel", "noIv"], pals.BestFirst().Select(p => p.Uid).ToArray());
    }

    [Fact]
    public void TheOwnedTargetListedFirst_IsTheOneWithTheBestIvs_NotTheHighestLevel()
    {
        var finder = new SpeciesPathFinder(Data.Value, Breeding.Value);
        var owned = new[] { Pal("weak", "SheepBall", 30, new Ivs(10, 10, 10)), Pal("strong", "SheepBall", 5, new Ivs(90, 95, 80)) };
        var query = new PathQuery("SheepBall", null, [], false, 2, true, 4, 3, OwnerScope.Everything);
        var result = finder.Find(query, owned, CancellationToken.None);
        Assert.Equal("strong", result.OwnedTargets![0].Uid);
    }
}
