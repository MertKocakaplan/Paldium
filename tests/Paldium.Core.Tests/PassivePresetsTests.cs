using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;
using Paldium.Core.Planning;

namespace Paldium.Core.Tests;

public class PassivePresetsTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static PalSpecies Pal(string id) => Data.Value.Pal(id);

    [Fact]
    public void EveryRole_HasTwoVariants_WithFourDistinctPassivesThatExistInTheBundle()
    {
        foreach (var role in RolePresets.Roles)
            foreach (var variant in new[] { PresetVariant.Best, PresetVariant.Easy })
            {
                var preset = RolePresets.Build(Data.Value, role, variant, Pal("Kitsunebi"));
                Assert.NotNull(preset);
                Assert.Equal(4, preset!.PassiveIds.Count);
                Assert.Equal(4, preset.PassiveIds.Distinct().Count());
                Assert.All(preset.PassiveIds, id => Assert.NotNull(Data.Value.Passive(id)));
            }
    }

    [Fact]
    public void BestSets_AreTheFixedSetsOfTheResearch()
    {
        string[] Ids(string role) => RolePresets.Build(Data.Value, role, PresetVariant.Best)!.PassiveIds.ToArray();
        Assert.Equal(["MutationPal_Immortal", "PAL_ALLAttack_up3", "Legend", "CoolTimeReduction_Up_1"], Ids(RolePresets.CombatBalanced));
        Assert.Equal(["WorldTree_CraftSpeed", "CraftSpeed_up3", "CraftSpeed_up2", "PAL_CorporateSlave"], Ids(RolePresets.WorkerNight));
        Assert.Equal(["MutationPal_Babysitter", "Test_PalEgg_HatchingSpeed_Up", "CraftSpeed_up3", "CraftSpeed_up2"], Ids(RolePresets.BreedingFarm));
        // The night worker's best set warns about the passives that cost something.
        Assert.Equal(["WorldTree_CraftSpeed", "PAL_CorporateSlave"], RolePresets.Build(Data.Value, RolePresets.WorkerNight, PresetVariant.Best)!.PenaltyPassives);
    }

    [Fact]
    public void EasySets_HoldOnlyPassivesThatCanAppearOnWildPals_AndTheStrongestOfTheirType()
    {
        foreach (var role in RolePresets.Roles)
        {
            var preset = RolePresets.Build(Data.Value, role, PresetVariant.Easy, Pal("Kitsunebi"))!;
            Assert.All(preset.PassiveIds, id => Assert.True(Data.Value.Passive(id).CanAppearRandomly, $"{role}: {id}"));
            Assert.All(preset.PassiveIds, id => Assert.False(Data.Value.Passive(id).MutationExclusive, $"{role}: {id}"));
        }

        var work = RolePresets.Build(Data.Value, RolePresets.WorkerNight, PresetVariant.Easy)!.PassiveIds;
        Assert.Equal(["CraftSpeed_up3", "CraftSpeed_up2"], work.Take(2)); // Remarkable Craftsmanship, Artisan
        Assert.Contains("Nocturnal", RolePresets.Build(Data.Value, RolePresets.WorkerDay, PresetVariant.Easy)!.PassiveIds);
    }

    [Theory]
    [InlineData("Kitsunebi", "ElementBoost_Fire_2_PAL", "EternalFlame")]
    [InlineData("Hedgehog", "ElementBoost_Thunder_2_PAL", "EternalFlame")]
    public void ElementRole_FollowsTheElementOfTheTarget(string species, string emperor, string? dual)
    {
        var target = Pal(species);
        var best = RolePresets.Build(Data.Value, RolePresets.CombatElement, PresetVariant.Best, target)!.PassiveIds;
        Assert.Contains(emperor, best);
        if (dual != null) Assert.Contains(dual, best);
        Assert.Contains("PAL_ALLAttack_up3", best);
        Assert.Contains("Legend", best);

        // Another element gives another set; without a target the element role does not exist.
        var other = RolePresets.Build(Data.Value, RolePresets.CombatElement, PresetVariant.Best, Pal("CowPal"))!.PassiveIds;
        Assert.NotEqual(best, other);
        Assert.Null(RolePresets.Build(Data.Value, RolePresets.CombatElement, PresetVariant.Best));
        Assert.Null(RolePresets.Build(Data.Value, RolePresets.CombatElement, PresetVariant.Easy));
    }

    [Fact]
    public void Origin_And_GuaranteedSpecies_DescribeWhereAPassiveComesFrom()
    {
        var data = Data.Value;
        Assert.Equal(PassiveOrigin.Wild, RolePresets.OriginOf(data.Passive("CraftSpeed_up3")));
        Assert.Equal(PassiveOrigin.NotWild, RolePresets.OriginOf(data.Passive("Legend")));
        Assert.Equal(PassiveOrigin.MutationOnly, RolePresets.OriginOf(data.Passive("MutationPal_Immortal")));
        Assert.Equal(PassiveOrigin.WorldTree, RolePresets.OriginOf(data.Passive("WorldTree_CraftSpeed")));

        var legend = RolePresets.GuaranteedOn(data, "Legend").Select(p => p.Id).ToList();
        Assert.Contains("JetDragon", legend);
        Assert.Contains("IceHorse", legend);
        Assert.Empty(RolePresets.GuaranteedOn(data, "CraftSpeed_up3"));
    }

    [Fact]
    public void SavedPresets_SurviveARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-presets-" + Guid.NewGuid().ToString("N"));
        try
        {
            new UserDataStore(dir).SavePresets([new UserDataStore.SavedPreset("Mine", ["CraftSpeed_up2", "CraftSpeed_up3"])]);
            var loaded = new UserDataStore(dir).LoadPresets();
            var preset = Assert.Single(loaded);
            Assert.Equal("Mine", preset.Name);
            Assert.Equal(["CraftSpeed_up2", "CraftSpeed_up3"], preset.PassiveIds);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
