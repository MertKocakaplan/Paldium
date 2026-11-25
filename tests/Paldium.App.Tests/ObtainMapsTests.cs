using Paldium.App.Pages.PaldexParts;
using Paldium.Core.Domain;

namespace Paldium.App.Tests;

/// <summary>T140: which of the two maps the "Where in the world" section of a species shows.</summary>
public class ObtainMapsTests
{
    private static ObtainInfo Of(string id) => PlannerStateServiceTests.GameDataForTests.Pals.Single(p => p.Id == id).Obtain!;

    [Fact]
    public void ASpeciesOnBothMaps_OffersBoth_PalpagosFirstAndShownFirst()
    {
        var deer = Of("Deer");

        Assert.Equal([ObtainMaps.Main, ObtainMaps.Tree], ObtainMaps.Of(deer));
        Assert.Equal(ObtainMaps.Main, ObtainMaps.Default(deer));
        foreach (var map in ObtainMaps.Of(deer))
        {
            var spawners = deer.Wild.Where(w => w.Points.Any(p => p.Map == map)).Select(w => w.Spawner).Distinct().Count();
            Assert.Equal(spawners, ObtainMaps.AreaCount(deer, map));
        }
    }

    [Fact]
    public void ASpeciesWhoseWildAreasAreOnTheWorldTree_ShowsThatMap_AlsoWithAnAlphaOnPalpagos()
    {
        var jormuntide = Of("Umihebi");

        Assert.Equal([ObtainMaps.Main, ObtainMaps.Tree], ObtainMaps.Of(jormuntide));
        Assert.Equal(ObtainMaps.Tree, ObtainMaps.Default(jormuntide));
        Assert.Equal(1, ObtainMaps.AreaCount(jormuntide, ObtainMaps.Main)); // the Alpha alone
        Assert.Equal(jormuntide.Wild.Select(w => w.Spawner).Distinct().Count(), ObtainMaps.AreaCount(jormuntide, ObtainMaps.Tree)); // areas are spawners, as in the list
    }

    [Fact]
    public void ASpeciesOfOneMap_HasOnlyThatMap()
    {
        var lamball = Of("SheepBall");
        Assert.Equal([ObtainMaps.Main], ObtainMaps.Of(lamball));
        Assert.Equal(ObtainMaps.Main, ObtainMaps.Default(lamball));

        var treeOnly = PlannerStateServiceTests.GameDataForTests.Pals
            .First(p => p.Obtain is { Bosses.Count: 0 } o && o.Wild.Count > 0 && o.Wild.All(w => w.Points.All(pt => pt.Map == "Tree")));
        Assert.Equal([ObtainMaps.Tree], ObtainMaps.Of(treeOnly.Obtain!));
        Assert.Equal(ObtainMaps.Tree, ObtainMaps.Default(treeOnly.Obtain!));
    }

    [Fact]
    public void ThePlaces_ListPalpagosFirst_ThenTheWorldTree_ThenTheOnesWithoutAMap_InTheirOrder()
    {
        MapPlace Place(string name, string? map) => new("wild", "Wild", name, "", name, map, []);
        var places = new[] { Place("t1", "Tree"), Place("m1", "MainMap"), Place("x", null), Place("t2", "Tree"), Place("m2", "MainMap") };

        Assert.Equal(["m1", "m2", "t1", "t2", "x"], ObtainMaps.Ordered(places).Select(p => p.Name));
    }

    [Fact]
    public void ThePreview_FramesTheDotsOfTheChosenMap()
    {
        var jormuntide = Of("Umihebi");
        var dots = WildDots.Preview(jormuntide.Wild, (pt, night) => ObtainMaps.Dot(pt, alpha: false, night), out var points, ObtainMaps.Tree);

        Assert.Equal(jormuntide.Wild.SelectMany(w => w.Points).Count(p => p.Map == "Tree"), points);
        Assert.All(dots, d => { Assert.InRange(d.Left, 0, 1); Assert.InRange(d.Top, 0, 1); Assert.Null(d.MapX); });
        var frame = MapFraming.Compute(dots);
        Assert.All(dots, d => { Assert.InRange(d.Left, frame.Left, frame.Left + frame.Width); Assert.InRange(d.Top, frame.Top, frame.Top + frame.Height + 0.25); });
        Assert.Empty(WildDots.Preview(jormuntide.Wild, (pt, night) => ObtainMaps.Dot(pt, false, night), out _, ObtainMaps.Main));
    }
}
