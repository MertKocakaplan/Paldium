using Paldium.App.Services;
using Paldium.Core.Data;
using Paldium.Core.Queries;

namespace Paldium.App.Tests;

public class SkillsStateTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(FindDataFolder()));

    private static string FindDataFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Paldium.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data");
    }

    [Fact]
    public void Defaults_RequireAllTags_AndSortByRankDescending()
    {
        var state = new SkillsStateService();

        Assert.Equal(SkillsStateService.TabPassives, state.ActiveTab);
        Assert.True(state.PassiveFilter.TagsMatchAll);
        Assert.Equal(PassiveSortKey.Rank, state.PassiveSort.Key);
        Assert.True(state.PassiveSort.Descending);
        Assert.Equal(ActiveSortKey.Power, state.ActiveSort.Key);
        Assert.True(state.ActiveSort.Descending);
    }

    [Fact]
    public void NormalizeTab_SupportsTurkishAndEnglishValues()
    {
        Assert.Equal(SkillsStateService.TabActives, SkillsStateService.NormalizeTab("aktifler"));
        Assert.Equal(SkillsStateService.TabActives, SkillsStateService.NormalizeTab("actives"));
        Assert.Equal(SkillsStateService.TabPassives, SkillsStateService.NormalizeTab("pasifler"));
        Assert.Equal(SkillsStateService.TabPassives, SkillsStateService.NormalizeTab("other"));
    }

    [Fact]
    public void ApplyQuery_WithPassive_SelectsPassive_AndClearsConflictingFilters()
    {
        var state = new SkillsStateService
        {
            ActiveTab = SkillsStateService.TabActives,
            PassiveOwnedOnly = true,
            PassiveFilter = new PassiveFilter { Text = "nonmatching-xyz", TagsMatchAll = true },
        };
        var target = Data.Value.Passives.First();

        var applied = state.ApplyQuery(null, target.Id, Data.Value, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.True(applied);
        Assert.Equal(SkillsStateService.TabPassives, state.ActiveTab);
        Assert.Equal(target.Id, state.SelectedPassiveId);
        Assert.False(state.PassiveOwnedOnly);
        Assert.Null(state.PassiveFilter.Text);
    }

    [Fact]
    public void ApplyQuery_WithForce_ReappliesAfterTabSwitch_AndEmptyQueryClearsCache()
    {
        var state = new SkillsStateService();
        var target = Data.Value.Passives.First();

        Assert.True(state.ApplyQuery(null, target.Id, Data.Value));
        state.ActiveTab = SkillsStateService.TabActives;
        state.SelectedPassiveId = null;

        Assert.False(state.ApplyQuery(null, target.Id, Data.Value));
        Assert.True(state.ApplyQuery(null, target.Id, Data.Value, force: true));
        Assert.Equal(SkillsStateService.TabPassives, state.ActiveTab);
        Assert.Equal(target.Id, state.SelectedPassiveId);

        Assert.False(state.ApplyQuery(null, null, Data.Value));
        Assert.True(state.ApplyQuery(null, target.Id, Data.Value));
    }
}
