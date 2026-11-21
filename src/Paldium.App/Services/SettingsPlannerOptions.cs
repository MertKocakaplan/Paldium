namespace Paldium.App.Services;

/// <summary>
/// How the Settings page's planner defaults (screens/settings.md) map to <see cref="PlannerSettings"/>: improvement time
/// off / 10 / 30 / 60 s, wild Pals none / 1 / 2 / 3+, steps 4 / 6 / 8 / 10 and cake none / special.
/// </summary>
public static class SettingsPlannerOptions
{
    public static readonly IReadOnlyList<int> TimeLimits = [0, 10, 30, 60];
    public static readonly IReadOnlyList<int> WildChoices = [0, 1, 2, 3];
    public static readonly IReadOnlyList<int> StepChoices = [4, 6, 8, 10];

    /// <summary>What "3+" stores as the wild Pal limit: in practice no limit (ARCHITECTURE section 13).</summary>
    public const int UnlimitedWild = 10;

    /// <summary>0 = no wild Pals; 1 and 2 as set; 3 stands for "3 or more".</summary>
    public static int WildChoice(PlannerSettings planner) => !planner.AllowWild ? 0 : Math.Clamp(planner.MaxWild, 0, 3);

    public static PlannerSettings WithWild(PlannerSettings planner, int choice) =>
        choice <= 0 ? planner with { AllowWild = false } : planner with { AllowWild = true, MaxWild = choice >= 3 ? UnlimitedWild : choice };

    public static PlannerSettings WithTimeLimit(PlannerSettings planner, int seconds) => planner with { TimeLimitSeconds = Math.Max(0, seconds) };

    public static PlannerSettings WithSteps(PlannerSettings planner, int steps) => planner with { MaxSteps = Math.Clamp(steps, 1, 10) };

    public static PlannerSettings WithSpecialCake(PlannerSettings planner, bool special) => planner with { AllowSpecialCake = special };
}
