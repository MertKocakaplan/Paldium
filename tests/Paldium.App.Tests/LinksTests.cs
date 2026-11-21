using Paldium.App.Components;

namespace Paldium.App.Tests;

public class LinksTests
{
    [Fact]
    public void ThePalLink_OpensThePaldexDetail_OfTheSpecies()
    {
        Assert.Equal("paldex?pal=SheepBall", Links.Pal("SheepBall"));
        Assert.Equal("paldex?pal=A%20B%26C", Links.Pal("A B&C"));
    }

    [Fact]
    public void ThePassiveLink_OpensSkills_AtThePassive()
    {
        Assert.Equal("skills?passive=CraftSpeed_up3", Links.Passive("CraftSpeed_up3"));
    }

    [Fact]
    public void TheCalculatorLink_CarriesBothParents()
    {
        Assert.Equal("calculator?a=Anubis&b=SheepBall", Links.CalculatorPair("Anubis", "SheepBall"));
    }
}
