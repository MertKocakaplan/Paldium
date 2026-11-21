using Paldium.App.Services;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.App.Tests;

public class TabAndPairTests
{
    private static OwnedPal Pal(string uid, string species, Gender gender, int level = 10, DateTimeOffset? seen = null) =>
        new(uid, null, species, gender, level, 1, [], null, [], [], null, null, new PalLocation(LocationKind.Palbox, null, null, null),
            PalSourceKind.Manual, "manual", seen ?? DateTimeOffset.UnixEpoch, false, false);

    [Fact]
    public void TheTable_IgnoresTheGallerySort()
    {
        var data = PlannerStateServiceTests.GameDataForTests;
        var lamball = Pal("a", "SheepBall", Gender.Female, seen: DateTimeOffset.UnixEpoch.AddDays(5));   // low Paldex number, seen later
        var anubis = Pal("b", "Anubis", Gender.Male, seen: DateTimeOffset.UnixEpoch.AddDays(1));          // high Paldex number, seen earlier
        var state = new MyPalsStateService();
        state.SetGallerySort(MyPalsStateService.SortAdded, descending: true);

        var cards = state.ApplyAll([anubis, lamball], data, "en");
        Assert.Equal(["a", "b"], cards.Select(p => p.Uid));      // most recently added first

        var table = state.ApplyAll([anubis, lamball], data, "en", tableView: true);
        Assert.Equal(["a", "b"], table.Select(p => p.Uid));       // the table sorts by SortKey (Pal number)

        state.SetTableSort(Paldium.Core.Queries.OwnedSortKey.Species, descending: true);
        var desc = state.ApplyAll([lamball, anubis], data, "en", tableView: true);
        Assert.Equal(["b", "a"], desc.Select(p => p.Uid));
    }

    [Fact]
    public void BothOwned_OpensTheOwnedPairWithRightGenders()
    {
        var state = new CalculatorStateService();
        var pals = new[]
        {
            Pal("m1", "Anubis", Gender.Male, level: 5),
            Pal("f1", "Anubis", Gender.Female, level: 9),
            Pal("f2", "SheepBall", Gender.Female, level: 3),
        };

        Assert.True(state.TryOpenOwnedPair("Anubis", GenderReq.Male, "SheepBall", GenderReq.Female, pals));
        Assert.True(state.OwnedModeA);
        Assert.True(state.OwnedModeB);
        Assert.Equal("m1", state.OwnedAKey);
        Assert.Equal("f2", state.OwnedBKey);
        Assert.Equal("SheepBall", state.SpeciesBId);
    }

    [Fact]
    public void BothOwned_SameSpecies_UsesTwoDifferentPals_AndFailsWithoutTheGender()
    {
        var state = new CalculatorStateService();
        var pals = new[] { Pal("m1", "Anubis", Gender.Male), Pal("f1", "Anubis", Gender.Female) };
        Assert.True(state.TryOpenOwnedPair("Anubis", GenderReq.Any, "Anubis", GenderReq.Any, pals));
        Assert.NotEqual(state.OwnedAKey, state.OwnedBKey);

        var none = new CalculatorStateService();
        Assert.False(none.TryOpenOwnedPair("Anubis", GenderReq.Female, "Anubis", GenderReq.Female, [pals[0]]));
        Assert.False(none.OwnedModeA);
    }
}
