using Paldium.Core.Data;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class SkillQueryTests
{
    private static readonly Lazy<GameData> Real = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static IGameData Data => Real.Value;

    private static List<string> PassiveIds(PassiveFilter? f = null, PassiveSort? s = null, string lang = "en") =>
        SkillQuery.Passives(Data, f ?? new PassiveFilter(), s ?? new PassiveSort(), lang).Select(p => p.Id).ToList();

    private static List<string> ActiveIds(ActiveFilter? f = null, ActiveSort? s = null, string lang = "en") =>
        SkillQuery.Actives(Data, f ?? new ActiveFilter(), s ?? new ActiveSort(), lang).Select(a => a.Id).ToList();

    [Fact]
    public void Passives_NoFilter_Returns115StandardPassives()
    {
        var all = PassiveIds();
        Assert.Equal(115, all.Count);
        Assert.Equal(115, Data.Passives.Count(p => p.IsStandard));
    }

    [Fact]
    public void Passives_DefaultSort_IsBestRankFirst()
    {
        var sorted = SkillQuery.Passives(Data, new PassiveFilter(), new PassiveSort(), "en");
        for (var i = 1; i < sorted.Count; i++) Assert.True(sorted[i - 1].Rank >= sorted[i].Rank);
        Assert.Equal(5, sorted[0].Rank);
        Assert.True(sorted[^1].Rank < 0);
    }

    [Fact]
    public void Passives_TagFilter_WorkSpeed_ListsOnlyPassivesThatImproveIt()
    {
        var ids = PassiveIds(new PassiveFilter { Tags = ["WorkSpeed"] });
        string[] expected = ["CraftSpeed_up1", "CraftSpeed_up2", "CraftSpeed_up3", "PAL_CorporateSlave", "PAL_conceited", "Rare", "WorldTree_CraftSpeed"];
        Assert.Equal(expected.Order().ToList(), ids.Order().ToList());

        // Musclehead only worsens work speed, so it is not a WorkSpeed passive.
        Assert.DoesNotContain("Noukin", ids);
        Assert.DoesNotContain("CraftSpeed_down2", ids);
    }

    [Fact]
    public void Passives_MultipleTags_MatchAnyOfThem()
    {
        var work = PassiveIds(new PassiveFilter { Tags = ["WorkSpeed"] });
        var sanity = PassiveIds(new PassiveFilter { Tags = ["Sanity"] });
        var both = PassiveIds(new PassiveFilter { Tags = ["WorkSpeed", "Sanity"] });

        Assert.Equal(work.Union(sanity).Order().ToList(), both.Order().ToList());
        Assert.True(both.Count > work.Count);
    }

    [Fact]
    public void Passives_NegativeTag_FindsTheNegativePassives()
    {
        var ids = PassiveIds(new PassiveFilter { Tags = ["Negative"] });
        Assert.Equal(Data.Passives.Count(p => p.Rank < 0), ids.Count);
        Assert.All(ids, id => Assert.True(Data.Passive(id).Rank < 0));
    }

    [Fact]
    public void Passives_RankRange_IsInclusive()
    {
        var ids = PassiveIds(new PassiveFilter { MinRank = 3, MaxRank = 4 });
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.InRange(Data.Passive(id).Rank, 3, 4));
        Assert.Equal(Data.Passives.Count(p => p.Rank is 3 or 4), ids.Count);

        Assert.All(PassiveIds(new PassiveFilter { MaxRank = -1 }), id => Assert.True(Data.Passive(id).Rank <= -1));
    }

    [Fact]
    public void Passives_TextSearch_FindsByNameDescriptionAndId_InBothLanguages()
    {
        Assert.Contains("CraftSpeed_up3", PassiveIds(new PassiveFilter { Text = "Remarkable Craftsmanship" }));
        Assert.Contains("CraftSpeed_up3", PassiveIds(new PassiveFilter { Text = "craftspeed_up3" }));
        Assert.Contains("CraftSpeed_up3", PassiveIds(new PassiveFilter { Text = "Work Speed +75%" }));

        var turkish = Data.Passive("CraftSpeed_up3").Names.Get("tr");
        Assert.Contains("CraftSpeed_up3", PassiveIds(new PassiveFilter { Text = turkish }, lang: "tr"));
        Assert.Empty(PassiveIds(new PassiveFilter { Text = "zzzzqqq" }));
    }

    [Fact]
    public void Passives_Filters_CombineWithAnd()
    {
        var ids = PassiveIds(new PassiveFilter { Tags = ["Attack"], MinRank = 4 });
        Assert.NotEmpty(ids);
        Assert.All(ids, id =>
        {
            var p = Data.Passive(id);
            Assert.Contains("Attack", p.Tags);
            Assert.True(p.Rank >= 4);
        });
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void Passives_SortByName_FollowsLanguage(string lang)
    {
        var sorted = SkillQuery.Passives(Data, new PassiveFilter(), new PassiveSort { Key = PassiveSortKey.Name, Descending = false }, lang);
        var comparer = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo(lang), ignoreCase: true);
        for (var i = 1; i < sorted.Count; i++)
            Assert.True(comparer.Compare(sorted[i - 1].Names.Get(lang), sorted[i].Names.Get(lang)) <= 0);
    }

    [Fact]
    public void Actives_NoFilter_Returns320()
    {
        Assert.Equal(320, ActiveIds().Count);
    }

    [Fact]
    public void Actives_ElementFilter_MatchesAnyOfTheSelected()
    {
        var water = ActiveIds(new ActiveFilter { Elements = ["Water"] });
        var fire = ActiveIds(new ActiveFilter { Elements = ["fire"] });
        var both = ActiveIds(new ActiveFilter { Elements = ["Water", "Fire"] });

        Assert.NotEmpty(water);
        Assert.All(water, id => Assert.Equal("Water", Data.Active(id).Element));
        Assert.Equal(water.Count + fire.Count, both.Count);
    }

    [Fact]
    public void Actives_InheritableAndFruitFilters()
    {
        var inheritable = ActiveIds(new ActiveFilter { Inheritable = true });
        var notInheritable = ActiveIds(new ActiveFilter { Inheritable = false });
        Assert.Equal(320, inheritable.Count + notInheritable.Count);
        Assert.NotEmpty(inheritable);
        Assert.NotEmpty(notInheritable);
        Assert.All(inheritable, id => Assert.True(Data.Active(id).CanInherit));

        var fruit = ActiveIds(new ActiveFilter { HasSkillFruit = true });
        Assert.All(fruit, id => Assert.True(Data.Active(id).HasSkillFruit));
        Assert.Contains("AquaJet", fruit);
    }

    [Fact]
    public void Actives_SortByPower_AscendingAndDescending()
    {
        var asc = SkillQuery.Actives(Data, new ActiveFilter(), new ActiveSort { Key = ActiveSortKey.Power }, "en");
        var desc = SkillQuery.Actives(Data, new ActiveFilter(), new ActiveSort { Key = ActiveSortKey.Power, Descending = true }, "en");
        for (var i = 1; i < asc.Count; i++) Assert.True(asc[i - 1].Power <= asc[i].Power);
        for (var i = 1; i < desc.Count; i++) Assert.True(desc[i - 1].Power >= desc[i].Power);
        Assert.Equal(asc.Max(a => a.Power), desc[0].Power);
    }

    [Fact]
    public void Actives_SortByCooldown_AscendingAndDescending()
    {
        var asc = SkillQuery.Actives(Data, new ActiveFilter(), new ActiveSort { Key = ActiveSortKey.Cooldown }, "en");
        var desc = SkillQuery.Actives(Data, new ActiveFilter(), new ActiveSort { Key = ActiveSortKey.Cooldown, Descending = true }, "en");
        for (var i = 1; i < asc.Count; i++) Assert.True(asc[i - 1].CooldownSeconds <= asc[i].CooldownSeconds);
        for (var i = 1; i < desc.Count; i++) Assert.True(desc[i - 1].CooldownSeconds >= desc[i].CooldownSeconds);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public void Actives_SortByName_FollowsLanguage(string lang)
    {
        var sorted = SkillQuery.Actives(Data, new ActiveFilter(), new ActiveSort { Key = ActiveSortKey.Name }, lang);
        var comparer = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo(lang), ignoreCase: true);
        for (var i = 1; i < sorted.Count; i++)
            Assert.True(comparer.Compare(sorted[i - 1].Names.Get(lang), sorted[i].Names.Get(lang)) <= 0);
    }

    [Fact]
    public void Actives_TextSearch_FindsByNameAndId()
    {
        Assert.Contains("AquaJet", ActiveIds(new ActiveFilter { Text = "Hydro Jet" }));
        Assert.Contains("AquaJet", ActiveIds(new ActiveFilter { Text = "aquajet" }));
        var turkish = Data.Active("AquaJet").Names.Get("tr");
        Assert.Contains("AquaJet", ActiveIds(new ActiveFilter { Text = turkish }, lang: "tr"));
        Assert.Empty(ActiveIds(new ActiveFilter { Text = "zzzzqqq" }));
    }

    [Fact]
    public void Passives_TagsMatchAll_RequiresAllSelectedTags()
    {
        var anyMountOrWork = PassiveIds(new PassiveFilter { Tags = ["Mount", "WorkSpeed"], TagsMatchAll = false });
        var allMountAndWork = PassiveIds(new PassiveFilter { Tags = ["Mount", "WorkSpeed"], TagsMatchAll = true });

        Assert.NotEmpty(anyMountOrWork);
        Assert.Empty(allMountAndWork);

        var attackAndDefense = PassiveIds(new PassiveFilter { Tags = ["Attack", "Defense"], TagsMatchAll = true });
        Assert.Contains("Legend", attackAndDefense);
        Assert.All(attackAndDefense, id =>
        {
            var tags = Data.Passive(id).Tags;
            Assert.Contains("Attack", tags);
            Assert.Contains("Defense", tags);
        });
    }

    [Fact]
    public void Passives_RanksAndObtainFilters_NarrowResults()
    {
        var ranks = PassiveIds(new PassiveFilter { Ranks = [4, -1] });
        Assert.NotEmpty(ranks);
        Assert.All(ranks, id => Assert.True(Data.Passive(id).Rank is 4 or -1));

        var mutation = PassiveIds(new PassiveFilter { MutationOnly = true });
        Assert.NotEmpty(mutation);
        Assert.All(mutation, id => Assert.True(Data.Passive(id).MutationExclusive));

        var surgery = PassiveIds(new PassiveFilter { SurgerySupported = true });
        Assert.All(surgery, id => Assert.True(Data.Passive(id).Surgery.Supported));
    }

    [Fact]
    public void Actives_PowerRange_FiltersByMinAndMax()
    {
        var ids = ActiveIds(new ActiveFilter { MinPower = 150, MaxPower = 400 });
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.InRange(Data.Active(id).Power, 150, 400));
    }

    [Fact]
    public void Pals_ActiveSkillFilter_FindsSpeciesLearningSkill()
    {
        var skillId = "AquaJet";
        var ids = PalQuery.Run(Data, new PalFilter { ActiveSkill = skillId }, new PalSort(), "en")
            .Select(p => p.Id)
            .ToList();
        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.Contains(Data.Pal(id).AttackLearnset, e => e.SkillId == skillId));
    }
}
