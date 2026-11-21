using Paldium.Core.Domain;
using Paldium.Import.Bridge;

namespace Paldium.Import.Tests;

public class BridgeSchemaTests
{
    private static string Example => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "palbox.example.json"));

    [Fact]
    public void ExampleFile_Parses_WithAllFieldsOfTheFirstPal()
    {
        var file = BridgeFile.Parse(Example);
        Assert.Equal(1, file.SchemaVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T20:00:00Z"), file.ExportedAtUtc);
        Assert.Equal("Example", file.PlayerName);
        Assert.Equal(5, file.Pals.Count);

        var pal = file.Pals[0];
        Assert.Equal("11111111222222223333333344444444", pal.InstanceId);
        Assert.Equal("CowPal", pal.CharacterId);
        Assert.Equal("Female", pal.Gender);
        Assert.Equal(23, pal.Level);
        Assert.Equal(1, pal.Rank);
        Assert.Equal(["CraftSpeed_up2"], pal.Passives);
        Assert.Equal(new BridgeTalents(70, 55, 80), pal.Talents);
        Assert.Equal(["Unique_CowPal_Charge"], pal.EquippedAttacks);
        Assert.Null(pal.Nickname);
        Assert.Equal(new BridgeLocation("Palbox", 0, 0), pal.Location);
    }

    [Fact]
    public void MissingOptionalFields_AreAllowed()
    {
        var pal = BridgeFile.Parse(Example).Pals[3];
        Assert.Equal("ChickenPal", pal.CharacterId);
        Assert.Null(pal.Gender);
        Assert.Null(pal.InstanceId);
        Assert.Empty(pal.Passives);
        Assert.Null(pal.Talents);
        Assert.Null(pal.Rank);
    }

    [Fact]
    public void LocationKinds_AreKnownLocationKinds_AndCharacterIdsAreGameIds()
    {
        var file = BridgeFile.Parse(Example);
        Assert.All(file.Pals, p => Assert.True(Enum.TryParse<LocationKind>(p.Location!.Kind, out _)));
        // "BOSS_Anubis" is a game row name: the reader maps it to the species with IGameData.TryNormalizePalId.
        Assert.True(TestSupport.GameData.TryNormalizePalId(file.Pals[1].CharacterId, out var id));
        Assert.Equal("Anubis", id);
        Assert.All(file.Pals, p => Assert.True(TestSupport.GameData.TryNormalizePalId(p.CharacterId, out _), p.CharacterId));
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 2, \"pals\": [] }")]
    [InlineData("not json")]
    [InlineData("{ \"schemaVersion\": 1, \"pals\": [ { \"level\": 3 } ] }")]
    [InlineData("")]
    public void UnsupportedOrBrokenFiles_AreRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => BridgeFile.Parse(json));
    }
}
