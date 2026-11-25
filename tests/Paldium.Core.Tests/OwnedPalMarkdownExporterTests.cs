using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Owned;

namespace Paldium.Core.Tests;

public class OwnedPalMarkdownExporterTests
{
    private static readonly GameData Data = GameDataLoader.Load(TestPaths.DataFolder);

    private static OwnedPal MakePal(
        string uid,
        string speciesId = "CowPal",
        Gender? gender = Gender.Female,
        int level = 15,
        int rank = 1,
        IReadOnlyList<string>? passives = null,
        Ivs? ivs = null,
        string? nickname = null,
        string? ownerName = null,
        LocationKind locKind = LocationKind.Palbox,
        int? page = 0,
        bool isExcluded = false,
        bool isOnExpedition = false,
        bool isAlpha = false) =>
        new(
            Uid: uid,
            InstanceId: uid,
            SpeciesId: speciesId,
            Gender: gender,
            Level: level,
            Rank: rank,
            Passives: passives ?? [],
            Ivs: ivs,
            EquippedAttacks: [],
            LearnedAttacks: [],
            Nickname: nickname,
            Owner: ownerName == null ? null : new PalOwner("p1", ownerName, null, null),
            Location: new PalLocation(locKind, null, 0, null, page, locKind == LocationKind.Base ? "MainBase" : null),
            Source: PalSourceKind.Save,
            SourceId: "src",
            SeenAtUtc: DateTimeOffset.UnixEpoch,
            IsEdited: false,
            IsExcluded: isExcluded,
            IsOnExpedition: isOnExpedition,
            IsAlpha: isAlpha);

    [Fact]
    public void EscapesPipesAndNewlinesInCells()
    {
        var pal = MakePal(
            "u1",
            speciesId: "Anubis",
            nickname: "Pipe|Name\nLine2",
            ownerName: "Player|One");

        var md = OwnedPalMarkdownExporter.Write(
            [pal],
            Data,
            new ExportContext("tr", "Save", null, "Tüm oyuncular", null, new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero)));

        Assert.Contains("Pipe\\|Name Line2", md);
        Assert.Contains("Player\\|One", md);
        // Every table row in the Pal table must have the same number of unescaped '|' separators.
        var tableLines = md.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("| ", StringComparison.Ordinal) && l.Contains("Pipe\\|Name", StringComparison.Ordinal))
            .ToList();
        Assert.Single(tableLines);
        var unescapedPipes = tableLines[0].Replace("\\|", "", StringComparison.Ordinal).Count(c => c == '|');
        Assert.Equal(16, unescapedPipes);
    }

    [Fact]
    public void PreservesInputOrderAndPlacesUnknownHumanSpeciesAtEnd()
    {
        var unknown = MakePal("u-human", speciesId: "Hunter_Bat", level: 30);
        var first = MakePal("u-1", speciesId: "SheepBall", level: 5);
        var second = MakePal("u-2", speciesId: "Anubis", level: 40, isAlpha: true);

        var mdTr = OwnedPalMarkdownExporter.Write(
            [unknown, first, second],
            Data,
            new ExportContext("tr", "Mod", null, null, null, new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero)));

        var rows = mdTr.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("| 1 ", StringComparison.Ordinal)
                     || l.StartsWith("| 2 ", StringComparison.Ordinal)
                     || l.StartsWith("| 3 ", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, rows.Count);
        Assert.Contains("Lamball", rows[0]);
        Assert.Contains("Anubis", rows[1]);
        Assert.Contains("| evet |", rows[1]);
        Assert.Contains("Hunter_Bat (insan/NPC, Paldex'te yok)", rows[2]);

        var mdEn = OwnedPalMarkdownExporter.Write(
            [unknown, first],
            Data,
            new ExportContext("en", "Mod", null, null, null, new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero)));
        Assert.Contains("Hunter_Bat (human/NPC, not in Paldex)", mdEn);
    }

    [Fact]
    public void GlossaryIncludesAllUsedPassivesWithAccurateCountsAndTierOrder()
    {
        var p1 = MakePal("u1", passives: ["Legend", "CraftSpeed_up2"]);
        var p2 = MakePal("u2", passives: ["CraftSpeed_up2", "PAL_Sanity_Down_1"], isExcluded: true);
        var p3 = MakePal("u3", passives: ["CraftSpeed_up2"], isOnExpedition: true, ivs: new Ivs(90, 80, 70));

        var md = OwnedPalMarkdownExporter.Write(
            [p1, p2, p3],
            Data,
            new ExportContext("tr", "Save", null, null, null, new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero)));

        Assert.Contains("## Pasif sözlüğü", md);
        var glossaryLines = md.Split("## Pasif sözlüğü")[1]
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Pasif", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, glossaryLines.Count);
        // Legend (Rank 4) first, CraftSpeed_up2 (Rank 3, count 3) second, PAL_Sanity_Down_1 (Rank 1, count 1) third.
        Assert.Contains("Legend", glossaryLines[0]);
        Assert.EndsWith("| 1 |", glossaryLines[0]);
        Assert.Contains("Artisan", glossaryLines[1]);
        Assert.EndsWith("| 3 |", glossaryLines[1]);
        Assert.Contains("Positive Thinker", glossaryLines[2]);
        Assert.EndsWith("| 1 |", glossaryLines[2]);

        Assert.Contains("| hayır |", md);
        Assert.Contains("| seferde |", md);
        Assert.Contains("| 90/80/70 |", md);
        Assert.Contains("| ? |", md);
    }

    [Fact]
    public void SupportsBothTurkishAndEnglishHeaders()
    {
        var pal = MakePal("u1", speciesId: "Anubis", gender: Gender.Male, rank: 3, isAlpha: true, ivs: new Ivs(50, 60, 70));

        var tr = OwnedPalMarkdownExporter.Write([pal], Data,
            new ExportContext("tr", "Kaynak", null, "Tüm oyuncular", null, new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero)));
        var en = OwnedPalMarkdownExporter.Write([pal], Data,
            new ExportContext("en", "Source", null, "All players", null, new DateTimeOffset(2026, 11, 25, 0, 0, 0, TimeSpan.Zero)));

        Assert.Contains("# Pal listesi (2026-11-25)", tr);
        Assert.Contains("| E |", tr);
        Assert.Contains("| evet |", tr);

        Assert.Contains("# Pal list (2026-11-25)", en);
        Assert.Contains("| M |", en);
        Assert.Contains("| yes |", en);
    }
}
