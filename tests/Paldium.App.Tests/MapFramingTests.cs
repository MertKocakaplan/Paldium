using Paldium.App.Pages.PaldexParts;

namespace Paldium.App.Tests;

public class MapFramingTests
{
    private static MapDot Dot(double left, double top, bool alpha = false) => new(left, top, 0.01, alpha, null, null);

    [Fact]
    public void ASingleArea_GetsTheMinimumWindow_AroundIt()
    {
        var frame = MapFraming.Compute([Dot(0.5, 0.5)]);
        Assert.Equal(MapFraming.MinWidth, frame.Width, 9);
        Assert.Equal(frame.Width / MapFrame.Aspect, frame.Height, 9);
        Assert.Equal(0.5 - frame.Width / 2, frame.Left, 9);
    }

    [Fact]
    public void TheWindow_HasThePaddingAndStaysInsideTheMap()
    {
        // Two points 0.5 apart horizontally: the box plus 4.6 % each side is 0.592 wide; the height follows the 16:10 shape.
        var frame = MapFraming.Compute([Dot(0.2, 0.4), Dot(0.7, 0.5)]);
        Assert.Equal(0.5 + 2 * MapFraming.Padding, frame.Width, 9);
        Assert.InRange(frame.Left, 0, 1 - frame.Width);
        Assert.InRange(frame.Top, 0, 1 - frame.Height);

        // Points at the very corner cannot push the window out of the picture.
        var corner = MapFraming.Compute([Dot(0.99, 0.99), Dot(0.97, 0.98)]);
        Assert.InRange(corner.Left, 0, 1 - corner.Width);
        Assert.InRange(corner.Top, 0, 1 - corner.Height);
        // Spread over the whole map: the whole map.
        Assert.Equal(1.0, MapFraming.Compute([Dot(0, 0), Dot(1, 1)]).Width, 9);
    }

    [Fact]
    public void PointsInTheWindow_LandInsideIt()
    {
        var dots = new[] { Dot(0.18, 0.64), Dot(0.2, 0.66, alpha: true) };
        var frame = MapFraming.Compute(dots);
        foreach (var dot in dots)
        {
            var style = frame.PointStyle(dot);
            var left = double.Parse(style.Split(';')[0].Replace("left:", "").TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(left, 0, 100);
        }
    }
}
