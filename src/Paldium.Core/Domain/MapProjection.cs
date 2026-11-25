namespace Paldium.Core.Domain;

/// <summary>
/// Projection between Palworld world units and normalized 0..1 map image coordinates for one map (<c>MainMap</c> or <c>Tree</c>).
/// North is up, so horizontal pixel axis is world Y and vertical pixel axis is inverted world X.
/// </summary>
public sealed record MapProjection(
    string Id,
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    bool HasGameCoordinates = false,
    double Ax = 0,
    double Ay = 0,
    double Ac = 0,
    double Bx = 0,
    double By = 0,
    double Bc = 0)
{
    // In-game coordinate matrix from PalCalc (MIT, Tyler Camp, commit 9c4d2b5c), matching WorldMapProjection.
    public static readonly MapProjection Main = new(
        "MainMap",
        -1099400.0, 349400.0, -724400.0, 724400.0,
        HasGameCoordinates: true,
        Ax: -4.830223727277094E-07, Ay: 0.0021796738568829717, Ac: -344.193826581459,
        Bx: 0.0021779338609583232, By: 1.3843765562632747E-06, Bc: 269.9073674619908);

    /// <summary>DT_WorldMapUIData row <c>Tree</c>; Palworld displays no in-game coordinate grid for the World Tree map.</summary>
    public static readonly MapProjection Tree = new(
        "Tree",
        347351.5, 689148.5, -818197.0, -476400.0,
        HasGameCoordinates: false);

    public bool Contains(double worldX, double worldY) =>
        worldX >= MinX && worldX <= MaxX && worldY >= MinY && worldY <= MaxY;

    /// <summary>Fractions 0..1 from the left and top edges of the map image (not clamped).</summary>
    public (double Left, double Top) ToFraction(double worldX, double worldY) =>
        ((worldY - MinY) / (MaxY - MinY), (MaxX - worldX) / (MaxX - MinX));

    public (double X, double Y) ToPixel(double worldX, double worldY, int width, int height)
    {
        var (left, top) = ToFraction(worldX, worldY);
        return (left * width, top * height);
    }

    public (double MapX, double MapY)? ToMapCoordinates(double worldX, double worldY) =>
        HasGameCoordinates ? (Ax * worldX + Ay * worldY + Ac, Bx * worldX + By * worldY + Bc) : null;

    public double[]? Matrix => HasGameCoordinates ? [Ax, Ay, Ac, Bx, By, Bc] : null;

    public double? LeftOfMapX(double mapX) =>
        HasGameCoordinates && Ay != 0 ? ((mapX - Ac) / Ay - MinY) / (MaxY - MinY) : null;

    public double? TopOfMapY(double mapY) =>
        HasGameCoordinates && Bx != 0 ? (MaxX - (mapY - Bc) / Bx) / (MaxX - MinX) : null;

    public static MapProjection ForMap(string? mapId) =>
        string.Equals(mapId, "Tree", StringComparison.OrdinalIgnoreCase) ? Tree : Main;
}
