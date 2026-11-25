using Paldium.App.Pages.PaldexParts;
using Paldium.App.Services;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.App.Tests;

public class DropModelTests
{
    private static readonly LocalizedText Names = new(new Dictionary<string, string> { ["en"] = "Thing" }, "en");

    private static PalDrop Drop((double Rate, int Min, int Max, int Level)? defeat = null, (int Rank, double Chance)? ranch = null) => new(
        defeat is { } d ? new DropInfo("Thing", Names, d.Rate, d.Min, d.Max, d.Level) : null,
        ranch is { } r ? new RanchDropAt(r.Rank, new RanchDrop("Thing", Names, null, 1, r.Chance, 1, 3)) : null);

    private static IReadOnlyList<string> Lines(PalDrop drop, string lang)
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "paldium-drop-" + Guid.NewGuid().ToString("N"), "settings.json"));
        store.Update(s => s with { Language = lang });
        try { return DropModel.Lines(drop, lang, new LanguageStringLocalizer(new LanguageService(store))); }
        finally { Directory.Delete(Path.GetDirectoryName(store.Path)!, true); }
    }

    [Fact]
    public void ADefeatDrop_ShowsTheAmountAndTheChance_AndTheLevelWhenOneIsNeeded()
    {
        Assert.Equal(["1–2 · %100"], Lines(Drop((1, 1, 2, 0)), "tr"));
        Assert.Equal(["1 · 0.5%"], Lines(Drop((0.005, 1, 1, 0)), "en"));
        Assert.Equal(["Sv 20+ 3 · %50"], Lines(Drop((0.5, 3, 3, 20)), "tr"));
    }

    [Fact]
    public void WithBothWays_TheDefeatLineComesFirst_AndTheRanchLineNamesTheFirstLevel()
    {
        Assert.Equal(["1 · %100", "Ağıl ★5'ten · %18"], Lines(Drop((1, 1, 1, 0), (5, 0.1778)), "tr"));
        Assert.Equal(["Ranch ★1+ · 18%"], Lines(Drop(ranch: (1, 0.1778)), "en"));
    }

    [Theory]
    [InlineData(1, "den")]
    [InlineData(2, "den")]
    [InlineData(3, "ten")]
    [InlineData(4, "ten")]
    [InlineData(5, "ten")]
    [InlineData(6, "dan")]
    [InlineData(7, "den")]
    [InlineData(8, "den")]
    [InlineData(9, "dan")]
    [InlineData(10, "dan")]
    public void TheTurkishSuffix_FollowsTheNumber(int level, string suffix) => Assert.Equal(suffix, DropModel.Ablative(level));

    [Fact]
    public void TheLink_CarriesTheSourceOnlyWhenOneIsChosen()
    {
        Assert.Equal("paldex?drop=PotatoSeeds", DropModel.Link("PotatoSeeds", PalDropSource.Any));
        Assert.Equal("paldex?drop=Wool&dropsrc=ranch", DropModel.Link("Wool", PalDropSource.Ranch));
        Assert.Equal("paldex?drop=Wool&dropsrc=defeat", DropModel.Link("Wool", PalDropSource.Defeat));
    }

    [Theory]
    [InlineData("ranch", PalDropSource.Ranch)]
    [InlineData("Defeat", PalDropSource.Defeat)]
    [InlineData(null, PalDropSource.Any)]
    [InlineData("nonsense", PalDropSource.Any)]
    [InlineData("7", PalDropSource.Any)]
    public void TheSourceOfALink_IsReadWithoutLetterCase(string? text, PalDropSource source) => Assert.Equal(source, DropModel.ParseSource(text));

    private static readonly PalSort ByName = new() { Key = PalSortKey.Name };

    [Fact]
    public void ChoosingAnItem_SortsByItsChance_AndRemovingItBringsTheOldSortBack()
    {
        var state = new PaldexSettings { Sort = ByName };

        var chosen = DropModel.Apply(state, new PalFilter { DropItem = "Wool" });
        Assert.Equal((PalSortKey.DropChance, true), (chosen.Sort.Key, chosen.Sort.Descending));

        // Another item, and the user's own sort in between: choosing an item selects the chance sort again, the first sort is still remembered.
        var other = DropModel.Apply(chosen with { Sort = new PalSort { Key = PalSortKey.Hp } }, new PalFilter { DropItem = "Milk" });
        Assert.Equal(PalSortKey.DropChance, other.Sort.Key);

        var removed = DropModel.Apply(other, new PalFilter());
        Assert.Equal(ByName, removed.Sort);
        Assert.Null(removed.SortBeforeDrop);
    }

    [Fact]
    public void ASortTheUserChoseWhileFiltering_StaysWhenTheItemIsRemoved()
    {
        var chosen = DropModel.Apply(new PaldexSettings { Sort = ByName }, new PalFilter { DropItem = "Wool" });
        var hp = new PalSort { Key = PalSortKey.Hp, Descending = true };

        Assert.Equal(hp, DropModel.Apply(chosen with { Sort = hp }, new PalFilter()).Sort);
    }

    [Fact]
    public void ChangingOtherFilters_OrOnlyTheSource_LeavesTheSortAlone()
    {
        var chosen = DropModel.Apply(new PaldexSettings { Sort = ByName }, new PalFilter { DropItem = "Wool" });
        var own = chosen with { Sort = new PalSort { Key = PalSortKey.Rarity } };

        var changed = DropModel.Apply(own, own.Filter with { DropSource = PalDropSource.Ranch, Text = "lam" });

        Assert.Equal(PalSortKey.Rarity, changed.Sort.Key);
        Assert.Equal(ByName, changed.SortBeforeDrop);
        Assert.Equal(PalDropSource.Ranch, changed.Filter.DropSource);
    }

    [Fact]
    public void TheStoredSettings_KeepTheItemAndTheRememberedSort()
    {
        var path = Path.Combine(Path.GetTempPath(), "paldium-drop-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            store.Update(s => s with { Paldex = DropModel.Apply(s.Paldex with { Sort = ByName }, new PalFilter { DropItem = "Wool", DropSource = PalDropSource.Ranch }) });

            var read = new SettingsStore(path).Current.Paldex;

            Assert.Equal(("Wool", PalDropSource.Ranch), (read.Filter.DropItem, read.Filter.DropSource));
            Assert.Equal(ByName, read.SortBeforeDrop);
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }
}
