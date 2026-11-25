using Paldium.App.Components;
using Paldium.Core.Data;
using Paldium.Core.Domain;

namespace Paldium.App.Tests;

public class PalPickerFormatTests
{
    private static readonly GameData Data = GameDataLoader.Load(AppContext.BaseDirectory + "data");

    private static OwnedPal MakePal(
        string speciesId = "Anubis",
        Gender? gender = Gender.Male,
        int level = 42,
        string? nickname = null,
        params string[] passives) =>
        new(
            Uid: "u1",
            InstanceId: "i1",
            SpeciesId: speciesId,
            Gender: gender,
            Level: level,
            Rank: 1,
            Passives: passives,
            Ivs: null,
            EquippedAttacks: [],
            LearnedAttacks: [],
            Nickname: nickname,
            Owner: null,
            Location: new Paldium.Core.Domain.PalLocation(LocationKind.Palbox, null, 0, null, 2),
            Source: PalSourceKind.Save,
            SourceId: "src",
            SeenAtUtc: DateTimeOffset.UnixEpoch,
            IsEdited: false,
            IsExcluded: false);

    [Fact]
    public void BuildOwnedAriaLabel_FormatsSingleSentenceWithPassivesInTurkish()
    {
        var pal = MakePal("Anubis", Gender.Male, 42, null, "CraftSpeed_up2", "CraftSpeed_up1");

        var label = PalPickerFormat.BuildOwnedAriaLabel(
            pal, Data, "tr", "Kutu 3", "Sv", "Erkek", "Dişi", "pasifler: {0}", "Pasif yok");

        Assert.Equal("Anubis, Erkek, Sv 42, Kutu 3, pasifler: Usta, Ciddi", label);
    }

    [Fact]
    public void BuildOwnedAriaLabel_UsesNoPassivesAndOmitsUnknownGender()
    {
        var pal = MakePal("Anubis", null, 10, "Nick");

        var label = PalPickerFormat.BuildOwnedAriaLabel(
            pal, Data, "en", "Party", "Lv", "Male", "Female", "passives: {0}", "No passives");

        Assert.Equal("Nick, Lv 10, Party, No passives", label);
    }

    [Fact]
    public void PassiveMatchesQuery_IsCaseAndTurkishDiacriticInsensitive()
    {
        Assert.True(PalPickerFormat.PassiveMatchesQuery(Data, "CraftSpeed_up2", "usta"));
        Assert.True(PalPickerFormat.PassiveMatchesQuery(Data, "CraftSpeed_up2", "artisan"));
        Assert.True(PalPickerFormat.PassiveMatchesQuery(Data, "CraftSpeed_up1", "ciddi"));
        Assert.False(PalPickerFormat.PassiveMatchesQuery(Data, "CraftSpeed_up2", "legend"));
        Assert.False(PalPickerFormat.PassiveMatchesQuery(Data, "CraftSpeed_up2", "   "));
    }
}
