// The WorldToMapMatrix values below are copied from PalCalc (PalCalc.Model/GameConstants.cs,
// https://github.com/tylercamp/palcalc, MIT License, Copyright 2024, Tyler Camp), commit 9c4d2b5c.
// They convert world coordinates to the in-game map coordinates shown at the bottom left of the world map.
//
// MIT License notice (PalCalc): Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files, to deal in the software without restriction, subject to the inclusion of
// the above copyright notice and this permission notice in all copies or substantial portions of the software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.

namespace Paldium.DataBuilder;

/// <summary>World position to map name and in-game map coordinates.</summary>
public static class MapTransform
{
    // DT_WorldMapUIData landScapeRealPositionMin/Max (T20 table survey).
    private const double MainMinX = -1099400, MainMaxX = 349400, MainMinY = -724400, MainMaxY = 724400;
    private const double TreeMinX = 347351.5, TreeMaxX = 689148.5, TreeMinY = -818197, TreeMaxY = -476400;

    private static readonly double[,] WorldToMapMatrix =
    {
        { -4.830223727277094E-07, 0.0021796738568829717, -344.193826581459 },
        { 0.0021779338609583232, 1.3843765562632747E-06, 269.9073674619908 },
        { 0, 0, 1 },
    };

    /// <summary>
    /// "MainMap" when the point is inside the main map bounds (map coordinates rounded to integers), "Tree" when inside
    /// the World Tree map (no map coordinates: the matrix is only validated for the main map), otherwise null.
    /// </summary>
    public static (string? Map, int? MapX, int? MapY) Locate(double x, double y)
    {
        if (x >= MainMinX && x <= MainMaxX && y >= MainMinY && y <= MainMaxY)
        {
            var mapX = WorldToMapMatrix[0, 0] * x + WorldToMapMatrix[0, 1] * y + WorldToMapMatrix[0, 2];
            var mapY = WorldToMapMatrix[1, 0] * x + WorldToMapMatrix[1, 1] * y + WorldToMapMatrix[1, 2];
            return ("MainMap", (int)Math.Round(mapX, MidpointRounding.AwayFromZero),
                (int)Math.Round(mapY, MidpointRounding.AwayFromZero));
        }

        if (x >= TreeMinX && x <= TreeMaxX && y >= TreeMinY && y <= TreeMaxY) return ("Tree", null, null);
        return (null, null, null);
    }
}
