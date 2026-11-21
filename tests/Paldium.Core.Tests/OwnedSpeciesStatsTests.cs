using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.Core.Tests;

public class OwnedSpeciesStatsTests
{
    private static OwnedPal Pal(string species, Gender? gender, LocationKind location = LocationKind.Palbox, string owner = "p1") =>
        new(Guid.NewGuid().ToString("N"), null, species, gender, 10, 1, [], null, [], [], null,
            new PalOwner(owner, owner, null, null), new PalLocation(location, null, null, null),
            PalSourceKind.Save, "w", DateTimeOffset.UnixEpoch, false, false);

    [Fact]
    public void Compute_CountsTotalMaleAndFemale_PerSpecies()
    {
        var stats = OwnedSpeciesStats.Compute(
        [
            Pal("Mozzarina", Gender.Male), Pal("Mozzarina", Gender.Male), Pal("Mozzarina", Gender.Female), Pal("CowPal", Gender.Female),
        ]);

        Assert.Equal(new OwnedSpeciesStats(3, 2, 1), stats["Mozzarina"]);
        Assert.Equal(new OwnedSpeciesStats(1, 0, 1), stats["CowPal"]);
        Assert.False(stats.ContainsKey("Anubis"));
    }

    [Fact]
    public void UnknownGender_CountsInTheTotalOnly()
    {
        var stats = OwnedSpeciesStats.Compute([Pal("CowPal", null), Pal("CowPal", Gender.Male)])["CowPal"];
        Assert.Equal(new OwnedSpeciesStats(2, 1, 0), stats);
        Assert.Equal(GenderState.MaleOnly, stats.Genders);
        Assert.Equal(GenderState.None, OwnedSpeciesStats.Compute([Pal("CowPal", null)])["CowPal"].Genders);
    }

    [Theory]
    [InlineData(2, 1, GenderState.Both, true)]
    [InlineData(1, 0, GenderState.MaleOnly, false)]
    [InlineData(0, 3, GenderState.FemaleOnly, false)]
    [InlineData(0, 0, GenderState.None, false)]
    public void GenderBadge_AndPair(int male, int female, GenderState expected, bool pair)
    {
        var stats = new OwnedSpeciesStats(male + female, male, female);
        Assert.Equal(expected, stats.Genders);
        Assert.Equal(pair, stats.HasPair);
    }

    [Fact]
    public void MissingGender_NeedsAnOwnedPal_ButNotForSingleGenderSpecies()
    {
        Assert.True(new OwnedSpeciesStats(2, 2, 0).IsMissingGender(0.5));
        Assert.True(new OwnedSpeciesStats(1, 0, 0).IsMissingGender(0.5)); // owned, gender unknown: still no pair
        Assert.False(new OwnedSpeciesStats(2, 1, 1).IsMissingGender(0.5));
        Assert.False(OwnedSpeciesStats.None.IsMissingGender(0.5));
        Assert.False(new OwnedSpeciesStats(1, 0, 1).IsMissingGender(0.0)); // only females exist
        Assert.False(new OwnedSpeciesStats(1, 1, 0).IsMissingGender(1.0)); // only males exist
    }

    [Fact]
    public void TheScope_DecidesWhatIsCounted()
    {
        // The counts are made from the scoped Pals, so Pals outside the scope (another player's Palbox, the Global Palbox) never count.
        var all = new[]
        {
            Pal("Mozzarina", Gender.Male, owner: "p1"), Pal("Mozzarina", Gender.Female, owner: "p2"), Pal("Mozzarina", Gender.Female, LocationKind.GlobalStorage, "p1"),
        };
        var scope = new OwnerScope(["p1"], OwnerScope.AllLocations.Where(l => l != LocationKind.GlobalStorage).ToList());
        var snapshot = new OwnedPalSnapshot(all, [], new Dictionary<string, PalOverride>(), [], 0, 1);

        var stats = OwnedSpeciesStats.Compute(OwnedPalRepository.Filter(snapshot, scope));

        Assert.Equal(new OwnedSpeciesStats(1, 1, 0), stats["Mozzarina"]);
    }
}
