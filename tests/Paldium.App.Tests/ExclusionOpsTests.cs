using Paldium.App.Pages.PlannerParts;
using Paldium.App.Services;
using Paldium.Core.Domain;
using Paldium.Core.Planning;

namespace Paldium.App.Tests;

public class ExclusionOpsTests
{
    [Fact]
    public void WithAndWithout_KeepTheQueryOtherwiseAsItWas()
    {
        var q = new PlannerSettings { TargetId = "CuteFox" };
        q = ExclusionOps.With(q, new PlanExclusion(PlanExclusion.GroupKind, "terraria"));
        q = ExclusionOps.With(q, new PlanExclusion(PlanExclusion.SpeciesKind, "Nox"));
        q = ExclusionOps.With(q, new PlanExclusion(PlanExclusion.UidKind, "abc"));
        q = ExclusionOps.With(q, new PlanExclusion(PlanExclusion.UidKind, "abc"));      // twice: once
        Assert.Equal(3, ExclusionOps.Count(q));
        Assert.Equal("CuteFox", q.TargetId);
        q = ExclusionOps.Without(q, new PlanExclusion(PlanExclusion.SpeciesKind, "nox"));
        Assert.Equal(2, ExclusionOps.Count(q));
        Assert.Equal(0, ExclusionOps.Count(ExclusionOps.Clear(q)));
    }

    [Theory]
    [InlineData("Hoocrates", "Hoocrates'i")]
    [InlineData("Nox", "Nox'u")]
    [InlineData("Chikipi", "Chikipi'yi")]
    [InlineData("Lamball", "Lamball'ı")]
    public void Accusative_FollowsTheLastVowel(string name, string expected) =>
        Assert.Equal(expected, ExclusionOps.Accusative(name, "tr"));

    [Fact]
    public void Accusative_IsTheNameItselfInEnglish() => Assert.Equal("Nox", ExclusionOps.Accusative("Nox", "en"));
}

public class NodeAlternativesTests
{
    private static OwnedPal Pal(string uid, string species, Gender g) =>
        new(uid, null, species, g, 1, 1, [], null, [], [], null, null, new PalLocation(LocationKind.Palbox, null, null, null),
            PalSourceKind.Manual, "manual", DateTimeOffset.UnixEpoch, false, false);

    [Theory]
    [InlineData("tr", "1.", "1")]
    [InlineData("tr", "1. ve 2.", "1,2")]
    [InlineData("en", "1, 2 and 3", "1,2,3")]
    public void JoinNumbers_ReadsNaturally(string lang, string expected, string numbers) =>
        Assert.Equal(expected, NodeAlternatives.JoinNumbers(numbers.Split(',').Select(int.Parse).ToList(), lang));

    [Fact]
    public void SamePal_InTwoSteps_IsCountedTwice()
    {
        var chi = Pal("chi", "ChickenPal", Gender.Male);
        var moz = Pal("moz", "CowPal", Gender.Female);
        var first = new BreedStep(new OwnedLeaf(chi), new OwnedLeaf(moz), "WizardOwl", GenderReq.Female, [], 0, null, 0.5, 2);
        var root = new BreedStep(new OwnedLeaf(chi), first, "CuteFox", GenderReq.Any, [], 0, null, 1, 1);
        var plan = new BreedingPlan(root, 2, 3, 0, false, [], []);
        Assert.Equal(2, NodeAlternatives.UseCounts(plan)["chi"]);
        Assert.Equal(1, NodeAlternatives.UseCounts(plan)["moz"]);
        Assert.Equal([1, 2], NodeAlternatives.StepsUsing(plan, "chi"));
    }
}
