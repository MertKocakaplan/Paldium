using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class OwnedPalFilterTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static OwnedPal Pal(
        string uid,
        string species,
        Gender? gender = Gender.Female,
        int level = 20,
        IReadOnlyList<string>? passives = null,
        Ivs? ivs = null,
        LocationKind location = LocationKind.Palbox,
        PalOwner? owner = null,
        PalSourceKind source = PalSourceKind.Save,
        string? nickname = null,
        bool isOnExpedition = false) =>
        new(
            uid, null, species, gender, level, 1,
            passives ?? [],
            ivs,
            [], [],
            nickname, owner,
            new PalLocation(location, null, 0, null, 0),
            source, source.ToString(),
            DateTimeOffset.UnixEpoch,
            IsEdited: false,
            IsExcluded: false,
            IsOnExpedition: isOnExpedition);

    [Fact]
    public void EmptyResult_RemovalCounts_ReflectSingleClauseRemoval()
    {
        var alice = new PalOwner("p1", "Alice", null, null);
        IReadOnlyList<OwnedPal> pals =
        [
            Pal("lamb-m-craft", "SheepBall", Gender.Male, passives: ["CraftSpeed_up2", "CoolTime_down1"], owner: alice),
            Pal("cat-m-craft", "PinkCat", Gender.Male, passives: ["CraftSpeed_up2"], owner: alice),
            Pal("chik-f-king", "ChickenPal", Gender.Female, passives: ["CoolTime_down1"], owner: alice),
        ];

        // No single Pal has CraftSpeed_up2 AND CoolTime_down1 AND Female.
        var filter = new OwnedPalFilter
        {
            Passives = ["CraftSpeed_up2", "CoolTime_down1"],
            Gender = OwnedPalFilter.GenderFemale,
        };

        Assert.Empty(filter.Apply(pals, Data.Value));

        var removals = filter.ComputeRemovals(pals, Data.Value).ToDictionary(r => r.Key, r => r.RemainingCount);
        // Removing CraftSpeed_up2: chik-f-king has CoolTime_down1 and Female -> 1
        Assert.Equal(1, removals[OwnedPalFilter.PassiveKeyPrefix + "CraftSpeed_up2"]);
        // Removing CoolTime_down1: neither male matches Female -> 0
        Assert.Equal(0, removals[OwnedPalFilter.PassiveKeyPrefix + "CoolTime_down1"]);
        // Removing Gender: lamb-m-craft has both passives -> 1
        Assert.Equal(1, removals[OwnedPalFilter.GenderKey]);
    }

    [Fact]
    public void Filters_CombineGroupsWithAnd_AndValuesWithinGroupWithOr()
    {
        var alice = new PalOwner("p1", "Alice", null, null);
        var bora = new PalOwner("p2", "Bora", null, null);
        IReadOnlyList<OwnedPal> pals =
        [
            Pal("p1-box", "SheepBall", Gender.Male, passives: ["CraftSpeed_up2"], ivs: new Ivs(80, 70, 60), location: LocationKind.Palbox, owner: alice, source: PalSourceKind.ModBridge, nickname: "Pamuk"),
            Pal("p2-exp", "PinkCat", Gender.Female, passives: ["CraftSpeed_up2"], ivs: new Ivs(40, 40, 40), location: LocationKind.Base, owner: bora, source: PalSourceKind.Save, isOnExpedition: true),
            Pal("unk-low", "ChickenPal", null, passives: [], ivs: null, location: LocationKind.Party, owner: null, source: PalSourceKind.Manual),
        ];

        var byLocAndOwner = new OwnedPalFilter
        {
            Locations = new HashSet<string>(StringComparer.Ordinal) { OwnedPalFilter.ExpeditionLocationKey, LocationKind.Party.ToString() },
            Owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "p2", OwnedPalFilter.UnknownOwnerKey },
        };
        Assert.Equal(["p2-exp", "unk-low"], byLocAndOwner.Apply(pals, Data.Value).Select(p => p.Uid));

        var byNickname = new OwnedPalFilter { SpeciesQuery = "pamuk", MinIvTotal = 200 };
        Assert.Equal(["p1-box"], byNickname.Apply(pals, Data.Value).Select(p => p.Uid));

        var byUnknownGender = new OwnedPalFilter { Gender = OwnedPalFilter.GenderUnknown };
        Assert.Equal(["unk-low"], byUnknownGender.Apply(pals, Data.Value).Select(p => p.Uid));

        var cleared = byLocAndOwner.WithoutClause(OwnedPalFilter.LocationKey);
        Assert.Empty(cleared.Locations);
        Assert.Equal(2, cleared.Owners.Count);
    }

    [Fact]
    public void WithoutClause_RemovesTargetedFilterOnly()
    {
        var filter = new OwnedPalFilter
        {
            SpeciesQuery = "Anubis",
            Passives = ["Legend", "CraftSpeed_up2"],
            Gender = OwnedPalFilter.GenderFemale,
            Locations = new HashSet<string>(StringComparer.Ordinal) { LocationKind.Palbox.ToString() },
            Sources = new HashSet<string>(StringComparer.Ordinal) { PalSourceKind.Save.ToString() },
            MinIvTotal = 180,
        };

        var withoutPassive = filter.WithoutClause(OwnedPalFilter.PassiveKeyPrefix + "Legend");
        Assert.Equal(["CraftSpeed_up2"], withoutPassive.Passives);
        Assert.Equal("Anubis", withoutPassive.SpeciesQuery);
        Assert.Equal(180, withoutPassive.MinIvTotal);

        var withoutMinIv = filter.WithoutClause(OwnedPalFilter.MinIvKey);
        Assert.Equal(0, withoutMinIv.MinIvTotal);
        Assert.Equal(OwnedPalFilter.GenderFemale, withoutMinIv.Gender);
    }
}
