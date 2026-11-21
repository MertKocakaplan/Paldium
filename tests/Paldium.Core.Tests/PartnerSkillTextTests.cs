using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Text;

namespace Paldium.Core.Tests;

public class PartnerSkillTextTests
{
    private static readonly Lazy<GameData> Real = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    private static PartnerSkillInfo Skill(string en, string tr = "") => new(
        new LocalizedText(new Dictionary<string, string> { ["en"] = "Skill" }, "x"),
        new LocalizedText(new Dictionary<string, string> { ["en"] = en, ["tr"] = tr.Length == 0 ? en : tr }, "x"), [])
    {
        NumbersByRank = new Dictionary<string, IReadOnlyList<double?>>
        {
            ["Passive1_EffectValue1"] = new double?[] { 100, 120, 140, 160, 200 },
            ["ReferencePassive1_EffectValue1"] = new double?[] { 1.5, 2.25, null, 4, 5 },
            ["ActiveSkillMainValueByRank"] = new double?[] { 1.1, 1.3, 1.6, 2, 2.5 },
            ["ActiveSkillOverWriteEffectTime"] = new double?[] { 10, 12, 14, 16, 20 },
        },
        TextsByRank = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string?>>>
        {
            ["ReferenceMsgId_RideSpeedUp"] = new Dictionary<string, IReadOnlyList<string?>>
            {
                ["en"] = new string?[] { "", "(Ride Speed Up: 10%)", "(Ride Speed Up: 12%)", "(Ride Speed Up: 15%)", "(Ride Speed Up: 20%)" },
                ["tr"] = new string?[] { "", "(Hız: %10)", "(Hız: %12)", "(Hız: %15)", "(Hız: %20)" },
            },
        },
    };

    [Theory]
    [InlineData("Carry +{Passive1_EffectValue1}.", 1, "Carry +100.")]
    [InlineData("Carry +{Passive1_EffectValue1}.", 5, "Carry +200.")]
    [InlineData("Attack +{ReferencePassive1_EffectValue1}%", 2, "Attack +2.25%")]
    [InlineData("Damage x{ActiveSkillMainValueByRank}", 3, "Damage x1.6")]
    [InlineData("Invisible {ActiveSkillOverWriteEffectTime} s", 4, "Invisible 16 s")]
    public void EachPlaceholderPattern_IsFilledForTheRank(string template, int rank, string expected)
    {
        Assert.Equal(expected, PartnerSkillText.Render(Skill(template), "en", rank));
    }

    [Fact]
    public void MsgIdPlaceholders_UseTheLanguageAndRank()
    {
        var skill = Skill("Rides. {ReferenceMsgId_RideSpeedUp}", "Biner. {ReferenceMsgId_RideSpeedUp}");
        Assert.Equal("Rides.", PartnerSkillText.Render(skill, "en", 1));
        Assert.Equal("Rides. (Ride Speed Up: 15%)", PartnerSkillText.Render(skill, "en", 4));
        Assert.Equal("Biner. (Hız: %12)", PartnerSkillText.Render(skill, "tr", 3));
    }

    [Fact]
    public void UnknownPlaceholderOrMissingRankValue_IsKept_AndRankIsClamped()
    {
        Assert.Equal("{Nothing} 100", PartnerSkillText.Render(Skill("{Nothing} {Passive1_EffectValue1}"), "en", 0));
        Assert.Equal("{ReferencePassive1_EffectValue1}", PartnerSkillText.Render(Skill("{ReferencePassive1_EffectValue1}"), "en", 3));
        Assert.Equal("200", PartnerSkillText.Render(Skill("{Passive1_EffectValue1}"), "en", 99));
        Assert.True(PartnerSkillText.HasUnresolved("a {b} c"));
        Assert.False(PartnerSkillText.HasUnresolved("a b c"));
    }

    [Fact]
    public void Anubis_Rank1_ShowsANumber_AndRankChangesIt()
    {
        var anubis = Real.Value.Pal("Anubis");
        var one = PartnerSkillText.Render(anubis, "en", 1)!;
        Assert.DoesNotContain("{", one);
        Assert.Contains("30%", one);
        Assert.Contains("50%", PartnerSkillText.Render(anubis, "en", 5)!);
        var tr = PartnerSkillText.Render(anubis, "tr", 1)!;
        Assert.DoesNotContain("{", tr);
        // The game's icon between the words became a space ("saldırısını Toprak").
        Assert.Contains("saldırısını Toprak", tr);
    }

    [Fact]
    public void AlmostEveryDescriptionWithPlaceholders_IsFullyResolvedAtRank1()
    {
        var withPlaceholders = 0;
        var unresolved = new List<string>();
        foreach (var pal in Real.Value.Pals)
            foreach (var lang in new[] { "en", "tr" })
            {
                var raw = pal.PartnerSkill!.Descriptions.Get(lang);
                if (!PartnerSkillText.HasUnresolved(raw)) continue;
                withPlaceholders++;
                if (PartnerSkillText.HasUnresolved(PartnerSkillText.Render(pal, lang, 1)!)) unresolved.Add($"{pal.Id}/{lang}");
            }

        Assert.True(withPlaceholders > 250, withPlaceholders.ToString());
        Assert.True(unresolved.Count <= withPlaceholders * 0.05, string.Join(", ", unresolved));
    }
}
