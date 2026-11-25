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
        MapProjection.Main.ToFraction(worldX, worldY);

    /// <summary>How many in-game map units the picture is wide (500 units between two grid lines).</summary>
    public static double MapUnitsAcross => MapProjection.Main.Ay * (MaxY - MinY);

    /// <summary>The six numbers of the world to map matrix (rows: x then y), for scripts that show the cursor's map coordinates.</summary>
    public static double[] Matrix => MapProjection.Main.Matrix!;

    /// <summary>The in-game map coordinates (the numbers at the bottom left of the world map) of a world position, not rounded.</summary>
    public static (double MapX, double MapY) ToMapCoordinates(double worldX, double worldY) =>
        MapProjection.Main.ToMapCoordinates(worldX, worldY)!.Value;

    /// <summary>The fraction from the left of the picture where the in-game map coordinate <paramref name="mapX"/> (a vertical line) lies.</summary>
    public static double LeftOfMapX(double mapX) => MapProjection.Main.LeftOfMapX(mapX)!.Value;

    /// <summary>The fraction from the top of the picture where the in-game map coordinate <paramref name="mapY"/> (a horizontal line) lies.</summary>
    public static double TopOfMapY(double mapY) => MapProjection.Main.TopOfMapY(mapY)!.Value;

    /// <summary>The same in pixels of a picture <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static (double X, double Y) ToPixel(double worldX, double worldY, int width, int height) =>
        MapProjection.Main.ToPixel(worldX, worldY, width, height);
}
