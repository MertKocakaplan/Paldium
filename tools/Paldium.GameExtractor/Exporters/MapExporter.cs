using System.IO.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Newtonsoft.Json.Linq;

namespace Paldium.GameExtractor.Exporters;

/// <summary>
/// The main world map picture (T101): <c>DT_WorldMapUIData</c> row <c>MainMap</c> points at the texture <c>T_WorldMap</c>. The texture is
/// decoded, scaled down to at most <see cref="MaxSize"/> pixels (box filter) and written as a PNG together with the world bounds of the
/// picture (<c>landScapeRealPositionMin/Max</c>), so the DataBuilder can place the in-game map coordinates (mapX/mapY) on it.
/// </summary>
public static class MapExporter
{
    public const int MaxSize = 2048;
    public const int LargeSize = 4096;
    private const string MainTexturePath = "Pal/Content/Pal/Texture/UI/Map/T_WorldMap.T_WorldMap";
    private const string TreeTexturePath = "Pal/Content/Pal/Texture/UI/Map/T_TreeMap.T_TreeMap";
    private const string UiDataTable = "Pal/Content/Pal/DataTable/WorldMapUIData/DT_WorldMapUIData";

    /// <summary>Writes <c>world-map.png</c> and returns its description (null, with a note, when the texture cannot be read).</summary>
    public static JObject? Export(DefaultFileProvider provider, string outFolder, List<string> warnings) =>
        ExportMap(provider, outFolder, MainTexturePath, "MainMap", "world-map",
            "Pal/Content/Pal/Texture/UI/Map/T_WorldMap (DT_WorldMapUIData row MainMap)", warnings);

    /// <summary>Writes <c>tree-map.png</c>, <c>tree-map-large.png</c> and returns its description (T138/T140).</summary>
    public static JObject? ExportTree(DefaultFileProvider provider, string outFolder, List<string> warnings) =>
        ExportMap(provider, outFolder, TreeTexturePath, "Tree", "tree-map",
            "Pal/Content/Pal/Texture/UI/Map/T_TreeMap (DT_WorldMapUIData row Tree)", warnings);

    private static JObject? ExportMap(
        DefaultFileProvider provider, string outFolder, string texturePath, string uiRowName,
        string fileBaseName, string sourceLabel, List<string> warnings)
    {
        UTexture2D? texture;
        try { texture = provider.LoadPackageObject<UTexture2D>(texturePath); }
        catch (Exception ex) { warnings.Add($"Map texture {texturePath} could not be loaded: " + ex.Message); return null; }
        var mip = texture?.GetFirstMip();
        if (texture == null || mip?.BulkData?.Data == null) { warnings.Add("Map texture not found or empty: " + texturePath); return null; }

        int sourceWidth = mip.SizeX, sourceHeight = mip.SizeY;
        byte[] rgba, large;
        int width, height, largeWidth, largeHeight;

        if (texture.Format == CUE4Parse.UE4.Assets.Exports.Texture.EPixelFormat.PF_DXT1)
        {
            var scale = Math.Max(1, (int)Math.Ceiling(Math.Max(sourceWidth, sourceHeight) / (double)MaxSize));
            rgba = DecodeDxt1(mip.BulkData.Data, sourceWidth, sourceHeight, scale, out width, out height);

            // The second, larger picture for the full screen viewer above x2 (T104): half the source size.
            var largeScale = Math.Max(1, (int)Math.Ceiling(Math.Max(sourceWidth, sourceHeight) / (double)LargeSize));
            large = DecodeDxt1(mip.BulkData.Data, sourceWidth, sourceHeight, largeScale, out largeWidth, out largeHeight);
        }
        else
        {
            var fullRgba = TextureDecoder.ToRgba(texture.Format, mip.BulkData.Data, sourceWidth, sourceHeight);
            if (fullRgba == null)
            {
                warnings.Add($"Map texture {texturePath} has unsupported format {texture.Format}.");
                return null;
            }
            (rgba, width, height) = TextureDecoder.Downscale(fullRgba, sourceWidth, sourceHeight, MaxSize);
            (large, largeWidth, largeHeight) = TextureDecoder.Downscale(fullRgba, sourceWidth, sourceHeight, LargeSize);
        }

        File.WriteAllBytes(Path.Combine(outFolder, fileBaseName + ".png"), PngWriter.Write(rgba, width, height));
        File.WriteAllBytes(Path.Combine(outFolder, fileBaseName + "-large.png"), PngWriter.Write(large, largeWidth, largeHeight));

        JToken? row = null;
        try { row = GameTables.LoadRows(provider, UiDataTable)[uiRowName]; }
        catch (Exception ex) { warnings.Add("DT_WorldMapUIData could not be read: " + ex.Message); }
        return new JObject
        {
            ["file"] = fileBaseName + ".png",
            ["width"] = width,
            ["height"] = height,
            ["largeFile"] = fileBaseName + "-large.png",
            ["largeWidth"] = largeWidth,
            ["largeHeight"] = largeHeight,
            ["sourceWidth"] = sourceWidth,
            ["sourceHeight"] = sourceHeight,
            ["source"] = sourceLabel,
            ["worldMinX"] = (double?)row?["landScapeRealPositionMin"]?["X"],
            ["worldMinY"] = (double?)row?["landScapeRealPositionMin"]?["Y"],
            ["worldMaxX"] = (double?)row?["landScapeRealPositionMax"]?["X"],
            ["worldMaxY"] = (double?)row?["landScapeRealPositionMax"]?["Y"],
        };
    }

    /// <summary>
    /// Decodes DXT1 (BC1, 8 bytes per 4x4 block) and averages <paramref name="scale"/> x <paramref name="scale"/> pixels into one
    /// (a box filter). The texture is a power of two, so the output size is exact.
    /// </summary>
    internal static byte[] DecodeDxt1(byte[] data, int width, int height, int scale, out int outWidth, out int outHeight)
    {
        outWidth = width / scale;
        outHeight = height / scale;
        var sums = new int[outWidth * outHeight * 3];
        var counts = new int[outWidth * outHeight];
        var blocksX = width / 4;
        var blocksY = height / 4;
        var palette = new (int R, int G, int B)[4];
        for (var by = 0; by < blocksY; by++)
        {
            for (var bx = 0; bx < blocksX; bx++)
            {
                var o = (by * blocksX + bx) * 8;
                int c0 = data[o] | (data[o + 1] << 8), c1 = data[o + 2] | (data[o + 3] << 8);
                var p0 = Rgb565(c0);
                var p1 = Rgb565(c1);
                palette[0] = p0;
                palette[1] = p1;
                if (c0 > c1)
                {
                    palette[2] = ((2 * p0.R + p1.R) / 3, (2 * p0.G + p1.G) / 3, (2 * p0.B + p1.B) / 3);
                    palette[3] = ((p0.R + 2 * p1.R) / 3, (p0.G + 2 * p1.G) / 3, (p0.B + 2 * p1.B) / 3);
                }
                else
                {
                    palette[2] = ((p0.R + p1.R) / 2, (p0.G + p1.G) / 2, (p0.B + p1.B) / 2);
                    palette[3] = (0, 0, 0);
                }

                var bits = (uint)(data[o + 4] | (data[o + 5] << 8) | (data[o + 6] << 16) | (data[o + 7] << 24));
                for (var py = 0; py < 4; py++)
                {
                    for (var px = 0; px < 4; px++)
                    {
                        var color = palette[(bits >> (2 * (py * 4 + px))) & 3];
                        var x = (bx * 4 + px) / scale;
                        var y = (by * 4 + py) / scale;
                        var i = y * outWidth + x;
                        sums[i * 3] += color.R; sums[i * 3 + 1] += color.G; sums[i * 3 + 2] += color.B;
                        counts[i]++;
                    }
                }
            }
        }

        var rgba = new byte[outWidth * outHeight * 4];
        for (var i = 0; i < outWidth * outHeight; i++)
        {
            var n = Math.Max(1, counts[i]);
            rgba[i * 4] = (byte)(sums[i * 3] / n);
            rgba[i * 4 + 1] = (byte)(sums[i * 3 + 1] / n);
            rgba[i * 4 + 2] = (byte)(sums[i * 3 + 2] / n);
            rgba[i * 4 + 3] = 255;
        }

        return rgba;
    }

    private static (int R, int G, int B) Rgb565(int c) =>
        (((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31);
}

/// <summary>A small PNG encoder (RGBA8, no interlace): the tool writes one picture and needs no imaging package.</summary>
internal static class PngWriter
{
    public static byte[] Write(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8; header[9] = 6; // 8 bit, RGBA
        WriteChunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter: none
            raw.Write(rgba, y * width * 4, width * 4);
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw.GetBuffer(), 0, (int)raw.Length);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24); buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8); buffer[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        output.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes.Concat(data).ToArray());
        var crcBytes = new byte[4];
        WriteInt(crcBytes, 0, unchecked((int)crc));
        output.Write(crcBytes);
    }
}
