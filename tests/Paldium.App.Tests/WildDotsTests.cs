using Paldium.App.Pages.PaldexParts;
using Paldium.Core.Domain;

namespace Paldium.App.Tests;

public class WildDotsTests
{
    private static WildSpawn Spawn(bool night, params (double X, double Y)[] points) =>
        new("s", "Grass", null, 1, 10, night, false, points.Select(p => new MapPoint(p.X, p.Y, 100, "MainMap", 1, 1)).ToList());

    private static MapDot Dot(MapPoint pt, bool night) => new(pt.X / 1000, pt.Y / 1000, 0.01, false, pt.MapX, pt.MapY, night);

    [Fact]
    public void NightFlag_ReachesTheDots_AndADayAndANightDotAtOnePlaceStayTwo()
    {
        var dots = WildDots.Preview([Spawn(night: false, (100, 100)), Spawn(night: true, (100, 100), (500, 500))], Dot, out var points);
        Assert.Equal(3, points);
        Assert.Equal(3, dots.Count);
        Assert.Equal(2, dots.Count(d => d.Night));
    }

    [Fact]
    public void AllNight_OnlyWhenEveryAreaIsANightOne()
    {
        Assert.True(WildDots.AllNight([Spawn(true, (1, 1)), Spawn(true, (2, 2))]));
        Assert.False(WildDots.AllNight([Spawn(true, (1, 1)), Spawn(false, (2, 2))]));
        Assert.False(WildDots.AllNight([]));
    }
}
