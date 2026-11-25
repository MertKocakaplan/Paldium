using System.Globalization;

namespace Paldium.Core.Map;

/// <summary>The id of a shop's marker in <c>map-points.json</c>, shared by the data builder and the "show on map" link (T137/T138).</summary>
public static class ShopMapIds
{
    public static string PointId(string shopId, double worldX, double worldY) =>
        string.Create(CultureInfo.InvariantCulture, $"shop:{shopId}:{Math.Round(worldX, 0)}_{Math.Round(worldY, 0)}");
}
