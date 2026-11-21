using Paldium.Core.Domain;

namespace Paldium.Core.Tests;

public class WorldMapProjectionTests
{
    [Fact]
    public void TheCorners_AndTheCentre_LandOnThePicture()
    {
        // North-west corner of the world bounds: the world X maximum is the top, the world Y minimum is the left.
        Assert.Equal((0.0, 0.0), WorldMapProjection.ToFraction(WorldMapProjection.MaxX, WorldMapProjection.MinY));
        Assert.Equal((1.0, 1.0), WorldMapProjection.ToFraction(WorldMapProjection.MinX, WorldMapProjection.MaxY));
        var centre = WorldMapProjection.ToFraction((WorldMapProjection.MinX + WorldMapProjection.MaxX) / 2, 0);
        Assert.Equal(0.5, centre.Left, 6);
        Assert.Equal(0.5, centre.Top, 6);
    }

    [Fact]
    public void AKnownPoint_HasItsPixel_OnThe2048Picture()
    {
        // World (-583140.9, -452305.9) (the Alpha point of the bundle sample): 18.78 % from the left, 64.37 % from the top.
        var (x, y) = WorldMapProjection.ToPixel(-583140.9, -452305.9, 2048, 2048);
        Assert.Equal(384.6, x, 1);
        Assert.Equal(1318.2, y, 1);
    }

    [Fact]
    public void MapCoordinates_MatchTheBundleAndTheirLinesLandOnThePicture()
    {
        // The Anubis Alpha of the bundle: world (-167000, 96350) is the in-game point (-134, -94) (DataBuilder MapTransform).
        var (mx, my) = WorldMapProjection.ToMapCoordinates(-167000, 96350);
        Assert.InRange(mx, -140, -128);
        Assert.InRange(my, -100, -88);
        // The vertical line of an in-game x lies where a point with that x projects to (up to the small cross term).
        var (left, top) = WorldMapProjection.ToFraction(-167000, 96350);
        Assert.Equal(left, WorldMapProjection.LeftOfMapX(mx), 3);
        Assert.Equal(top, WorldMapProjection.TopOfMapY(my), 3);
    }

    [Fact]
    public void InGameCoordinates_AgreeWithThePictureAxes()
    {
        // The in-game x grows with the world Y and the in-game y with the world X (PalCalc matrix): left grows with Y, top shrinks with X.
        var a = WorldMapProjection.ToFraction(0, -100000);
        var b = WorldMapProjection.ToFraction(0, 100000);
        Assert.True(b.Left > a.Left);
        var c = WorldMapProjection.ToFraction(-100000, 0);
        var d = WorldMapProjection.ToFraction(100000, 0);
        Assert.True(d.Top < c.Top);
    }
}
