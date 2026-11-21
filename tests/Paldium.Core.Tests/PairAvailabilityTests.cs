using Paldium.Core.Breeding;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.Core.Tests;

public class PairAvailabilityTests
{
    private static OwnedPal Pal(string uid, string species, Gender? gender = Gender.Female) =>
        new(uid, null, species, gender, 10, 1, [], null, [], [], null, null,
            new PalLocation(LocationKind.Palbox, null, null, null), PalSourceKind.Manual, "manual",
            DateTimeOffset.UnixEpoch, false, false);

    private static ParentPair Pair(string a, string b, GenderReq ra = GenderReq.Any, GenderReq rb = GenderReq.Any) =>
        new(a, b, ra, rb);

    [Fact]
    public void BothSpeciesOwned_FormsThePair()
    {
        var owned = new[] { Pal("1", "LazyDragon"), Pal("2", "WhiteMoth_Neutral") };
        Assert.True(PairAvailability.CanForm(owned, Pair("LazyDragon", "WhiteMoth_Neutral")));
        Assert.True(PairAvailability.CanForm(owned, Pair("WhiteMoth_Neutral", "LazyDragon")));
    }

    [Fact]
    public void MissingASpecies_CannotFormThePair()
    {
        var owned = new[] { Pal("1", "LazyDragon") };
        Assert.False(PairAvailability.CanForm(owned, Pair("LazyDragon", "WhiteMoth_Neutral")));
        Assert.False(PairAvailability.CanForm([], Pair("LazyDragon", "WhiteMoth_Neutral")));
    }

    [Fact]
    public void SameSpeciesPair_NeedsTwoDifferentPals()
    {
        Assert.False(PairAvailability.CanForm([Pal("1", "JetDragon")], Pair("JetDragon", "JetDragon")));
        Assert.True(PairAvailability.CanForm([Pal("1", "JetDragon"), Pal("2", "JetDragon")], Pair("JetDragon", "JetDragon")));
    }

    [Fact]
    public void GenderRequirement_MustBeMetByDistinctPals()
    {
        var pair = Pair("CatMage", "FoxMage", GenderReq.Female, GenderReq.Male);

        Assert.True(PairAvailability.CanForm([Pal("1", "CatMage", Gender.Female), Pal("2", "FoxMage", Gender.Male)], pair));
        Assert.False(PairAvailability.CanForm([Pal("1", "CatMage", Gender.Male), Pal("2", "FoxMage", Gender.Male)], pair));
        Assert.False(PairAvailability.CanForm([Pal("1", "CatMage", Gender.Female), Pal("2", "FoxMage", Gender.Female)], pair));
    }

    [Fact]
    public void UnknownGender_FitsAnyRequirement()
    {
        var pair = Pair("CatMage", "FoxMage", GenderReq.Female, GenderReq.Male);
        Assert.True(PairAvailability.CanForm([Pal("1", "CatMage", null), Pal("2", "FoxMage", null)], pair));
    }

    [Fact]
    public void SameSpeciesWithGenders_NeedsOneOfEach()
    {
        var pair = Pair("Sekhmet", "Sekhmet", GenderReq.Female, GenderReq.Male);
        Assert.False(PairAvailability.CanForm([Pal("1", "Sekhmet", Gender.Female)], pair));
        Assert.True(PairAvailability.CanForm([Pal("1", "Sekhmet", Gender.Female), Pal("2", "Sekhmet", Gender.Male)], pair));
    }

    [Fact]
    public void SpeciesIds_AreMatchedIgnoringCase_UnderTurkishCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(PairAvailability.CanForm([Pal("1", "ICEHORSE"), Pal("2", "CowPal")], Pair("IceHorse", "CowPal")));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
