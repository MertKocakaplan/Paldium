namespace Paldium.Core.Domain;

/// <summary>
/// Where a world position falls on the main world map picture (<c>data/map/world-map.png</c>, T101). The picture covers the bounds of
/// <c>DT_WorldMapUIData</c> row <c>MainMap</c> (<c>landScapeRealPositionMin/Max</c>); north is up, so the picture's horizontal axis is the
/// world Y axis and its vertical axis the world X axis, upside down.
/// </summary>
public static class WorldMapProjection
{
    public const double MinX = -1099400.0, MaxX = 349400.0, MinY = -724400.0, MaxY = 724400.0;

    /// <summary>Fractions 0..1 from the left and from the top of the picture (not clamped).</summary>
    public static (double Left, double Top) ToFraction(double worldX, double worldY) =>
        ((worldY - MinY) / (MaxY - MinY), (MaxX - worldX) / (MaxX - MinX));

    // The world to in-game map coordinates matrix is copied from PalCalc (PalCalc.Model/GameConstants.cs, https://github.com/tylercamp/palcalc,
    // MIT License, Copyright 2024, Tyler Camp, commit 9c4d2b5c), like in tools/Paldium.DataBuilder/MapTransform.cs.
    private const double Ax = -4.830223727277094E-07, Ay = 0.0021796738568829717, Ac = -344.193826581459;
    private const double Bx = 0.0021779338609583232, By = 1.3843765562632747E-06, Bc = 269.9073674619908;

    /// <summary>How many in-game map units the picture is wide (500 units between two grid lines).</summary>
    public static double MapUnitsAcross => Ay * (MaxY - MinY);

    /// <summary>The six numbers of the world to map matrix (rows: x then y), for scripts that show the cursor's map coordinates.</summary>
    public static double[] Matrix => [Ax, Ay, Ac, Bx, By, Bc];

    /// <summary>The in-game map coordinates (the numbers at the bottom left of the world map) of a world position, not rounded.</summary>
    public static (double MapX, double MapY) ToMapCoordinates(double worldX, double worldY) =>
        (Ax * worldX + Ay * worldY + Ac, Bx * worldX + By * worldY + Bc);

    /// <summary>The fraction from the left of the picture where the in-game map coordinate <paramref name="mapX"/> (a vertical line) lies.</summary>
    public static double LeftOfMapX(double mapX) => ((mapX - Ac) / Ay - MinY) / (MaxY - MinY);

    /// <summary>The fraction from the top of the picture where the in-game map coordinate <paramref name="mapY"/> (a horizontal line) lies.</summary>
    public static double TopOfMapY(double mapY) => (MaxX - (mapY - Bc) / Bx) / (MaxX - MinX);

    /// <summary>The same in pixels of a picture <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static (double X, double Y) ToPixel(double worldX, double worldY, int width, int height)
    {
        var (left, top) = ToFraction(worldX, worldY);
        return (left * width, top * height);
    }
}
