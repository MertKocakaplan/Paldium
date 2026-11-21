using Paldium.App.Services;

namespace Paldium.App.Tests;

public class SettingsPlannerOptionsTests
{
    [Theory]
    [InlineData(false, 2, 0)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 2, 2)]
    [InlineData(true, 3, 3)]
    [InlineData(true, 8, 3)]
    [InlineData(true, 0, 0)]
    public void TheWildChoice_ReadsAllowWildAndMaxWild(bool allow, int max, int expected)
    {
        Assert.Equal(expected, SettingsPlannerOptions.WildChoice(new PlannerSettings { AllowWild = allow, MaxWild = max }));
    }

    [Fact]
    public void ChoosingWild_SetsBothFields()
    {
        var none = SettingsPlannerOptions.WithWild(new PlannerSettings { AllowWild = true, MaxWild = 2 }, 0);
        Assert.False(none.AllowWild);
        var two = SettingsPlannerOptions.WithWild(none, 2);
        Assert.True(two.AllowWild);
        Assert.Equal(2, two.MaxWild);
    }

    [Fact]
    public void TheTimeLimit_CanBeOff_AndTheOtherDefaultsAreSet()
    {
        var planner = new PlannerSettings();
        Assert.Equal(0, SettingsPlannerOptions.WithTimeLimit(planner, 0).TimeLimitSeconds);
        Assert.Equal(60, SettingsPlannerOptions.WithTimeLimit(planner, 60).TimeLimitSeconds);
        Assert.Equal(8, SettingsPlannerOptions.WithSteps(planner, 8).MaxSteps);
        Assert.True(SettingsPlannerOptions.WithSpecialCake(planner, true).AllowSpecialCake);
        Assert.Equal([0, 10, 30, 60], SettingsPlannerOptions.TimeLimits);
        Assert.Equal([4, 6, 8, 10], SettingsPlannerOptions.StepChoices);
    }
}
