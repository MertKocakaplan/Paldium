using System.Globalization;
using Paldium.Core.Data;
using Paldium.Core.Domain;
using Paldium.Core.Queries;

namespace Paldium.Core.Tests;

public class TextFoldTests
{
    private static readonly Lazy<GameData> Data = new(() => GameDataLoader.Load(TestPaths.DataFolder));

    [Theory]
    [InlineData("Saldırı", "saldiri")]
    [InlineData("SALDIRI", "saldiri")]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("Çalışma hızı", "calisma hizi")]
    [InlineData("Dayanıklılık", "dayaniklilik")]
    [InlineData("Zırh", "zirh")]
    [InlineData("Öğütücü ÜĞŞ", "ogutucu ugs")]
    [InlineData("Anubis", "anubis")]
    public void Fold_MakesTurkishLettersPlain(string text, string expected)
    {
        Assert.Equal(expected, TextFold.Fold(text));
    }

    [Fact]
    public void Fold_GivesTheSameResultUnderTurkishCulture()
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("icehorse", TextFold.Fold("ICEHORSE"));
            Assert.Equal("saldiri", TextFold.Fold("SALDIRI"));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }
    }

    [Theory]
    [InlineData("dayaniklilik")]
    [InlineData("DAYANIKLILIK")]
    [InlineData("Dayanıklılık")]
    [InlineData("sonsuz dayanıklılık")]
    public void PassiveSearch_FindsTurkishNames_WithPlainLetters(string query)
    {
        var ids = SkillQuery.Passives(Data.Value, new PassiveFilter { Text = query }, new PassiveSort(), "tr").Select(p => p.Id).ToList();
        Assert.Contains("Stamina_Up_1", ids);
    }

    [Fact]
    public void PassiveSearch_FindsZirh_AndStillKeepsEnglishAndIdSearches()
    {
        var plain = SkillQuery.Passives(Data.Value, new PassiveFilter { Text = "zirh" }, new PassiveSort(), "tr").Select(p => p.Id).ToList();
        Assert.Contains("MutationPal_ExplosionResist", plain);
        Assert.Contains("CraftSpeed_up3", SkillQuery.Passives(Data.Value, new PassiveFilter { Text = "craftspeed_up3" }, new PassiveSort(), "en").Select(p => p.Id));
    }

    [Fact]
    public void ActiveSearch_FindsEveryTurkishNameFromItsPlainAsciiForm()
    {
        var withTurkishLetters = Data.Value.Actives.Where(a => a.Names.Get("tr").Any(c => "ıİçÇşŞğĞöÖüÜ".Contains(c))).ToList();
        Assert.NotEmpty(withTurkishLetters);
        foreach (var skill in withTurkishLetters.Take(30))
        {
            var ascii = TextFold.Fold(skill.Names.Get("tr"));
            Assert.Contains(skill.Id, SkillQuery.Actives(Data.Value, new ActiveFilter { Text = ascii }, new ActiveSort(), "tr").Select(a => a.Id));
        }
    }

    [Fact]
    public void PalSearch_StillFindsEnglishNamesAndIds_AndTheNicknameStyleMatch()
    {
        Assert.Contains(Data.Value.Pals, p => PalTextSearch.Matches(p, "ANUBIS"));
        Assert.Contains(Data.Value.Pals, p => PalTextSearch.Matches(p, "mozz"));
        Assert.True(TextFold.Contains("Çıtır", "CITIR"));
        Assert.False(TextFold.Contains("Çıtır", "zzz"));
    }

    [Fact]
    public void PalSearch_FindsTurkishPalNames_FromTheirPlainAsciiForm()
    {
        var turkish = Data.Value.Pals.Where(p => p.Names.Get("tr").Any(c => "ıİçÇşŞğĞöÖüÜ".Contains(c))).ToList();
        Assert.NotEmpty(turkish);
        foreach (var pal in turkish.Take(40))
            Assert.True(PalTextSearch.Matches(pal, TextFold.Fold(pal.Names.Get("tr")).ToUpperInvariant()), pal.Id);
    }
}
