using System.Globalization;
using PalCalc.Model;
using Paldium.Core.Domain;
using Paldium.Import.Saves;
using DomainGender = Paldium.Core.Domain.Gender;

namespace Paldium.Import.Tests;

public class PalCalcMappingTests
{
    private static PalInstance Instance(
        string species = "CowPal", PalGender gender = PalGender.FEMALE, int level = 20, int rank = 1,
        string[]? passives = null, LocationType location = LocationType.Palbox)
    {
        var palDb = new Pal { InternalName = species, Name = species };
        return new PalInstance
        {
            InstanceId = Guid.NewGuid().ToString(),
            Pal = palDb,
            Level = level,
            Rank = rank,
            Gender = gender,
            NickName = null,
            OwnerPlayerId = "player-1",
            PassiveSkills = (passives ?? []).Select(p => (PalCalc.Model.PassiveSkill)new UnrecognizedPassiveSkill(p)).ToList(),
            ActiveSkills = [],
            EquippedActiveSkills = [],
            IV_HP = 70,
            IV_Shot = 55,
            IV_Defense = 80,
            Location = new PalCalc.Model.PalLocation { Type = location, ContainerId = "c-1", Index = 3 },
        };
    }

    private static PalCalcMapping NewMapping() => new(TestSupport.GameData);

    private static OwnedPal? Map(PalCalcMapping mapping, PalInstance pal, PalOwner? owner = null, string? baseId = null) =>
        mapping.Map(pal, "save:w:" + pal.InstanceId, PalSourceKind.Save, "w", DateTimeOffset.UnixEpoch, owner, baseId);

    [Fact]
    public void MapsTheBasicFields()
    {
        var owner = new PalOwner("player-1", "Alice", "g-1", "Guild");
        var pal = Map(NewMapping(), Instance(level: 33, rank: 3, passives: ["CraftSpeed_up3", "Rare"]), owner, "base-9")!;

        Assert.Equal("CowPal", pal.SpeciesId);
        Assert.Equal(DomainGender.Female, pal.Gender);
        Assert.Equal(33, pal.Level);
        Assert.Equal(3, pal.Rank);
        Assert.Equal(["CraftSpeed_up3", "Rare"], pal.Passives);
        Assert.Equal(new Ivs(70, 55, 80), pal.Ivs);
        Assert.Equal(owner, pal.Owner);
        Assert.Equal(PalSourceKind.Save, pal.Source);
        Assert.Equal(new Paldium.Core.Domain.PalLocation(LocationKind.Palbox, "c-1", 3, "base-9", 0), pal.Location);
    }

    [Theory]
    [InlineData(LocationType.Palbox, 280, 9, 10)]
    [InlineData(LocationType.Palbox, 29, 0, 29)]
    [InlineData(LocationType.DimensionalPalStorage, 61, 2, 1)]
    public void ARunningIndexIsSplitIntoPageAndSlotInThePage(LocationType type, int index, int page, int slot)
    {
        var pal = Instance(location: type);
        pal.Location = new PalCalc.Model.PalLocation { Type = type, ContainerId = "c-1", Index = index };
        var mapped = Map(NewMapping(), pal)!;
        Assert.Equal(page, mapped.Location.Page);
        Assert.Equal(slot, mapped.Location.SlotIndex);
    }

    [Fact]
    public void PartySlots_HaveNoPage()
    {
        var pal = Instance(location: LocationType.PlayerParty);
        var mapped = Map(NewMapping(), pal)!;
        Assert.Null(mapped.Location.Page);
        Assert.Equal(3, mapped.Location.SlotIndex);
    }

    [Fact]
    public void UnknownSpecies_IsSkipped_AndRemembered()
    {
        var mapping = NewMapping();
        Assert.Null(Map(mapping, Instance("TotallyNewPal")));
        Assert.Null(Map(mapping, Instance("totallynewpal")));
        Assert.NotNull(Map(mapping, Instance("CowPal")));
        Assert.Single(mapping.SkippedUnknownSpecies);
    }

    [Fact]
    public void UnknownPassives_AreDropped_KnownOnesKeepTheirCanonicalId()
    {
        var mapping = NewMapping();
        var pal = Map(mapping, Instance(passives: ["craftspeed_up3", "SomethingNew", "CraftSpeed_up3"]))!;
        Assert.Equal(["CraftSpeed_up3"], pal.Passives);
        Assert.Equal(["SomethingNew"], mapping.DroppedUnknownPassives);
    }

    [Theory]
    [InlineData(PalGender.MALE, DomainGender.Male)]
    [InlineData(PalGender.FEMALE, DomainGender.Female)]
    public void Gender_IsMapped(PalGender input, DomainGender expected)
    {
        Assert.Equal(expected, Map(NewMapping(), Instance(gender: input))!.Gender);
    }

    [Fact]
    public void MissingGender_BecomesUnknown()
    {
        Assert.Null(Map(NewMapping(), Instance(gender: PalGender.NONE))!.Gender);
    }

    [Theory]
    [InlineData(LocationType.PlayerParty, LocationKind.Party)]
    [InlineData(LocationType.Palbox, LocationKind.Palbox)]
    [InlineData(LocationType.Base, LocationKind.Base)]
    [InlineData(LocationType.ViewingCage, LocationKind.ViewingCage)]
    [InlineData(LocationType.DimensionalPalStorage, LocationKind.DimensionalStorage)]
    [InlineData(LocationType.GlobalPalStorage, LocationKind.GlobalStorage)]
    [InlineData(LocationType.Custom, LocationKind.Unknown)]
    public void LocationTypes_AreMapped(LocationType input, LocationKind expected)
    {
        Assert.Equal(expected, PalCalcMapping.MapLocation(input));
        Assert.Equal(expected, Map(NewMapping(), Instance(location: input))!.Location.Kind);
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        var instance = Instance(level: 0, rank: 9);
        instance.IV_HP = 250;
        instance.IV_Shot = -5;
        var pal = Map(NewMapping(), instance)!;
        Assert.Equal(1, pal.Level);
        Assert.Equal(5, pal.Rank);
        Assert.Equal(new Ivs(100, 0, 80), pal.Ivs);
    }

    [Fact]
    public void SpeciesIds_AreMatchedWithoutCultureRules_UnderTurkishCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var pal = Map(NewMapping(), Instance("ICEHORSE", passives: ["CRAFTSPEED_UP3"]))!;
            Assert.Equal("IceHorse", pal.SpeciesId);
            Assert.Equal(["CraftSpeed_up3"], pal.Passives);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("BOSS_Anubis", true)]
    [InlineData("boss_CowPal", true)]
    [InlineData("Anubis", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SaveAlphaReader_DetectsBossPrefix(string? characterId, bool expected)
    {
        Assert.Equal(expected, SaveAlphaReader.IsBossCharacterId(characterId));
    }
}
