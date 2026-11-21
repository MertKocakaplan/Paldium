using Paldium.Core.Domain;
using System.Diagnostics;
using Paldium.Core.Data;

namespace Paldium.Core.Tests;

// CPU heavy or timing sensitive tests run one after another (the bundle load test has a one second limit).
[Collection("Heavy")]
public class GameDataTests
{
    private static readonly Lazy<GameData> Shared = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    [Fact]
    public void Load_ReadsBundleInUnderOneSecond()
    {
        var sw = Stopwatch.StartNew();
        var data = GameDataLoader.Load(TestPaths.DataFolder);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"Load took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(299, data.Pals.Count);
        Assert.Equal(115, data.Passives.Count(p => p.IsStandard));
        Assert.Equal(320, data.Actives.Count);
        Assert.Equal(9, data.Elements.Count);
        Assert.Equal("1.0.5", data.Meta.GameVersion);
    }

    [Fact]
    public void Pal_IsCaseInsensitive_AndHasTurkishName()
    {
        var data = Shared.Value;
        Assert.False(string.IsNullOrWhiteSpace(data.Pal("CowPal").Names.Get("tr")));
        Assert.Equal("CowPal", data.Pal("cowpal").Id);
        Assert.Equal("Sekhmet", data.Pal("SEKHMET").Id);
    }

    [Fact]
    public void UnknownIds_ThrowKeyNotFound()
    {
        var data = Shared.Value;
        Assert.Throws<KeyNotFoundException>(() => data.Pal("NoSuchPal"));
        Assert.Throws<KeyNotFoundException>(() => data.Passive("NoSuchPassive"));
        Assert.Throws<KeyNotFoundException>(() => data.Active("NoSuchActive"));
    }

    [Theory]
    [InlineData("CowPal", "CowPal")]
    [InlineData("boss_cowpal", "CowPal")]
    [InlineData("BOSS_CowPal", "CowPal")]
    [InlineData("Boss_CowPal", "CowPal")]
    [InlineData("Raid_CowPal", "CowPal")]
    [InlineData("Gym_CowPal", "CowPal")]
    [InlineData("  BOSS_LazyDragon_Electric ", "LazyDragon_Electric")]
    public void TryNormalizePalId_StripsPrefixesCaseInsensitively(string raw, string expected)
    {
        Assert.True(Shared.Value.TryNormalizePalId(raw, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("BOSS_")]
    [InlineData("BOSS_NoSuchPal")]
    [InlineData("NoSuchPal")]
    public void TryNormalizePalId_RejectsUnknown(string raw)
    {
        Assert.False(Shared.Value.TryNormalizePalId(raw, out var id));
        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void IsWildCatchable_UsesObtainMethodsAndCapturability()
    {
        var real = Shared.Value;
        Assert.False(real.IsWildCatchable("NoSuchPal"));
        Assert.True(real.IsWildCatchable("Anubis"));

        GameData With(Func<PalSpecies, PalSpecies> change) => new(real.Meta, real.Mechanics,
            real.Pals.Select(p => p.Id == "Anubis" ? change(p) : p).ToList(), real.Passives, real.Actives, real.Elements, real.WorkTypes);
        ObtainInfo Only(params string[] methods) => new(methods, [], [], [], [], [], [], [], false);

        Assert.False(With(p => p with { Obtain = Only("TowerBoss", "SameSpeciesOnly") }).IsWildCatchable("Anubis"));
        Assert.True(With(p => p with { Obtain = Only("Cage") }).IsWildCatchable("Anubis"));
        Assert.True(With(p => p with { Obtain = Only("Raid") }).IsWildCatchable("Anubis"));
        // Not capturable: never counts, whatever the methods say.
        Assert.False(With(p => (p with { Obtain = Only("Wild") }) with { IsUncapturable = true }).IsWildCatchable("Anubis"));
        // No obtain data (schema 1): falls back to the wild level range.
        Assert.True(With(p => p with { Obtain = null, WildLevel = new LevelRange(1, 5) }).IsWildCatchable("Anubis"));
        Assert.False(With(p => p with { Obtain = null, WildLevel = null }).IsWildCatchable("Anubis"));
    }

    [Fact]
    public void Mechanics_AreMappedWithIntKeys()
    {
        var m = Shared.Value.Mechanics;
        Assert.Equal(4, m.PassiveInheritWeights[1]);
        Assert.Equal(1, m.PassiveInheritWeights[4]);
        Assert.Equal(4, m.PassiveRandomAddWeights[0]);
        Assert.Equal(0.05, m.AlphaRate);
        Assert.Equal(0.01, m.Mutation.Rate);
        Assert.Equal(8, m.EggSizeMinRarity["huge"]);
    }

    [Fact]
    public void Pal_MapsDetailFields()
    {
        var sekhmet = Shared.Value.Pal("Sekhmet");
        Assert.Equal(870, sekhmet.BreedingPower);
        Assert.Equal(new LevelRangeCheck(37, 80), new LevelRangeCheck(sekhmet.WildLevel!.Min, sekhmet.WildLevel.Max));
        Assert.Equal(6, sekhmet.Work["handiwork"]);
        Assert.Equal("icons/pals/Sekhmet.png", sekhmet.IconPath);
        Assert.NotNull(sekhmet.PartnerSkill);
        Assert.Equal("Desert Empress", sekhmet.PartnerSkill!.Names.Get("en"));
        Assert.NotNull(sekhmet.Obtain);
        Assert.Equal(2638, sekhmet.Price);
    }

    private sealed record LevelRangeCheck(int Min, int Max);

    [Fact]
    public void LocalizedText_FallsBackLanguageThenEnglishThenId()
    {
        var text = new LocalizedText(new Dictionary<string, string>
        {
            ["en"] = "Fire",
            ["tr"] = "Ateş",
            ["de"] = "-",
            ["fr"] = "  ",
        }, "FallbackId");

        Assert.Equal("Ateş", text.Get("tr"));
        Assert.Equal("Ateş", text.Get("TR"));
        Assert.Equal("Ateş", text.Get("tr-TR"));
        Assert.Equal("Fire", text.Get("de"));
        Assert.Equal("Fire", text.Get("fr"));
        Assert.Equal("Fire", text.Get("ja"));
        Assert.Equal("Fire", text.Get(""));

        var onlyDash = new LocalizedText(new Dictionary<string, string> { ["en"] = "-", ["tr"] = "-" }, "SomeId");
        Assert.Equal("SomeId", onlyDash.Get("tr"));
        Assert.Equal("SomeId", onlyDash.Get("en"));
        Assert.False(onlyDash.TryGet("tr", out _));
    }

    [Fact]
    public void Load_RejectsUnsupportedSchemaVersion()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pb-gd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "gamedata.json"), "{\"meta\":{\"schemaVersion\":99}}");
            var ex = Assert.Throws<InvalidDataException>(() => GameDataLoader.Load(dir));
            Assert.Contains("schema version 99", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_ReadsSchema2File()
    {
        var data = GameDataLoader.Load(TestPaths.DataFolder);
        Assert.Equal(2, data.Meta.SchemaVersion);
        var anubis = data.Pals.Single(p => p.Id == "Anubis");
        Assert.Equal(["Earth"], anubis.Elements);
        Assert.NotNull(anubis.PartnerSkill);
        Assert.NotEmpty(data.Pals.Single(p => p.Id == "CowPal").Drops);
    }

    [Fact]
    public void Load_ReadsSchema1File_NewFieldsEmpty()
    {
        // A schema 1 file: the committed bundle with the version patched and the schema 2 fields removed.
        var dir = Path.Combine(Path.GetTempPath(), "pb-gd1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.DataFolder, "gamedata.json")))!;
            root["meta"]!["schemaVersion"] = 1;
            foreach (var pal in root["pals"]!.AsArray())
            {
                var o = pal!.AsObject();
                o["elements"] = new System.Text.Json.Nodes.JsonArray();
                o["partnerSkill"] = null;
                o.Remove("drops");
                o.Remove("isUncapturable");
            }

            foreach (var passive in root["passives"]!.AsArray()) passive!.AsObject().Remove("effects");
            File.WriteAllText(Path.Combine(dir, "gamedata.json"), root.ToJsonString());
            File.Copy(Path.Combine(TestPaths.DataFolder, "breeding.json"), Path.Combine(dir, "breeding.json"));

            var data = GameDataLoader.Load(dir);
            Assert.Equal(1, data.Meta.SchemaVersion);
            Assert.All(data.Pals, p =>
            {
                Assert.Empty(p.Elements);
                Assert.Null(p.PartnerSkill);
                Assert.Empty(p.Drops);
                Assert.False(p.IsUncapturable);
            });
            Assert.All(data.Passives, p => Assert.Empty(p.Effects));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Terms_AreExposedByTheLoader()
    {
        Assert.True(Shared.Value.TryGetTerm("BreedingFarm", out var farm));
        Assert.Equal("Üretim Çiftliği", farm.Get("tr"));
        Assert.Equal("Breeding Farm", farm.Get("en"));
        Assert.False(Shared.Value.TryGetTerm("NoSuchTerm", out _));
    }

    [Fact]
    public void Load_MissingFolderOrFile_GivesClearErrors()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pb-gd-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(() => GameDataLoader.Load(missing));

        Directory.CreateDirectory(missing);
        try
        {
            Assert.Throws<FileNotFoundException>(() => GameDataLoader.Load(missing));
            File.WriteAllText(Path.Combine(missing, "gamedata.json"), "not json");
            Assert.Throws<InvalidDataException>(() => GameDataLoader.Load(missing));
        }
        finally
        {
            Directory.Delete(missing, recursive: true);
        }
    }
}
