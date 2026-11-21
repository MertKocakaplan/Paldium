using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class OwnedPalSearchTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static OwnedPal Pal(string species, string[] passives, string? nickname = null) =>
        new("u", null, species, Gender.Female, 15, 1, passives, null, [], [], nickname, null, new PalLocation(LocationKind.Palbox, null, null, null),
            PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    [Fact]
    public void APassiveNameFindsThePal_InBothLanguages_WithFolding()
    {
        var swift = Data.Value.Passives.First(p => p.Names.Get("en") == "Swift");
        var pal = Pal("SheepBall", [swift.Id]);
        Assert.True(OwnedPalSearch.Matches(Data.Value, pal, "Swift"));
        Assert.True(OwnedPalSearch.Matches(Data.Value, pal, "sWIFT"));
        var turkish = swift.Names.Get("tr");
        Assert.True(OwnedPalSearch.Matches(Data.Value, pal, turkish));
        Assert.True(OwnedPalSearch.Matches(Data.Value, pal, TextFold.Fold(turkish)));
        Assert.True(OwnedPalSearch.Matches(Data.Value, pal, swift.Id));
    }

    [Fact]
    public void ThePalWithoutThePassive_IsNotFound_AndTheSpeciesAndNicknameStillMatch()
    {
        var swift = Data.Value.Passives.First(p => p.Names.Get("en") == "Swift");
        var withSwift = Pal("SheepBall", [swift.Id]);
        var without = Pal("SheepBall", [], "Fluffy");
        Assert.True(OwnedPalSearch.Matches(Data.Value, withSwift, "Swift"));
        Assert.False(OwnedPalSearch.Matches(Data.Value, without, "Swift"));
        Assert.True(OwnedPalSearch.Matches(Data.Value, without, "fluf"));
        Assert.True(OwnedPalSearch.Matches(Data.Value, without, "lamball"));
        Assert.True(OwnedPalSearch.Matches(Data.Value, without, ""));
    }
}
