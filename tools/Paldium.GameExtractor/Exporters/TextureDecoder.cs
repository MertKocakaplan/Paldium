using CUE4Parse.UE4.Assets.Exports.Texture;

namespace Paldium.GameExtractor.Exporters;

/// <summary>Reads the pixel formats of the game's UI icons (DXT1, DXT5, BGRA8) into RGBA8 and scales them down; no imaging package (see <see cref="MapExporter"/>).</summary>
internal static class TextureDecoder
{
    public static byte[]? ToRgba(EPixelFormat format, byte[] data, int width, int height) => format switch
    {
        EPixelFormat.PF_B8G8R8A8 => Bgra(data, width, height),
        EPixelFormat.PF_DXT1 => Blocks(data, width, height, 8, alpha: false),
        EPixelFormat.PF_DXT5 => Blocks(data, width, height, 16, alpha: true),
        _ => null,
    };

    private static byte[] Bgra(byte[] data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = data[i * 4 + 2]; rgba[i * 4 + 1] = data[i * 4 + 1]; rgba[i * 4 + 2] = data[i * 4]; rgba[i * 4 + 3] = data[i * 4 + 3];
        }

        return rgba;
    }

    private static byte[] Blocks(byte[] data, int width, int height, int blockBytes, bool alpha)
    {
        var rgba = new byte[width * height * 4];
        var palette = new (int R, int G, int B, int A)[4];
        var alphas = new int[8];
        for (var by = 0; by < (height + 3) / 4; by++)
        {
            for (var bx = 0; bx < (width + 3) / 4; bx++)
            {
                var o = (by * ((width + 3) / 4) + bx) * blockBytes;
                var colorOffset = alpha ? o + 8 : o;
                int c0 = data[colorOffset] | (data[colorOffset + 1] << 8), c1 = data[colorOffset + 2] | (data[colorOffset + 3] << 8);
                var (r0, g0, b0) = Rgb565(c0);
                var (r1, g1, b1) = Rgb565(c1);
                palette[0] = (r0, g0, b0, 255);
                palette[1] = (r1, g1, b1, 255);
                if (c0 > c1 || alpha)
                {
                    palette[2] = ((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3, 255);
                    palette[3] = ((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3, 255);
                }
                else
                {
                    palette[2] = ((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2, 255);
                    palette[3] = (0, 0, 0, 0);
                }

                var colorBits = (uint)(data[colorOffset + 4] | (data[colorOffset + 5] << 8) | (data[colorOffset + 6] << 16) | (data[colorOffset + 7] << 24));
                ulong alphaBits = 0;
                if (alpha)
                {
                    int a0 = data[o], a1 = data[o + 1];
                    alphas[0] = a0; alphas[1] = a1;
                    if (a0 > a1) for (var i = 1; i <= 6; i++) alphas[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                    else
                    {
                        for (var i = 1; i <= 4; i++) alphas[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                        alphas[6] = 0; alphas[7] = 255;
                    }

                    for (var i = 0; i < 6; i++) alphaBits |= (ulong)data[o + 2 + i] << (8 * i);
                }

                for (var py = 0; py < 4; py++)
                {
                    for (var px = 0; px < 4; px++)
                    {
                        var x = bx * 4 + px;
                        var y = by * 4 + py;
                        if (x >= width || y >= height) continue;
                        var index = py * 4 + px;
                        var color = palette[(colorBits >> (2 * index)) & 3];
                        var a = alpha ? alphas[(int)((alphaBits >> (3 * index)) & 7)] : color.A;
                        var at = (y * width + x) * 4;
                        rgba[at] = (byte)color.R; rgba[at + 1] = (byte)color.G; rgba[at + 2] = (byte)color.B; rgba[at + 3] = (byte)a;
                    }
                }
            }
        }

        return rgba;
    }

    private static (int R, int G, int B) Rgb565(int c) =>
        (((c >> 11) & 31) * 255 / 31, ((c >> 5) & 63) * 255 / 63, (c & 31) * 255 / 31);

    /// <summary>Box filter down to at most <paramref name="size"/> pixels on the longer side (alpha weighted).</summary>
    public static (byte[] Rgba, int Width, int Height) Downscale(byte[] rgba, int width, int height, int size)
    {
        if (Math.Max(width, height) <= size) return (rgba, width, height);
        var scale = Math.Max(width, height) / (double)size;
        var outWidth = Math.Max(1, (int)Math.Round(width / scale));
        var outHeight = Math.Max(1, (int)Math.Round(height / scale));
        var result = new byte[outWidth * outHeight * 4];
        for (var oy = 0; oy < outHeight; oy++)
        {
            for (var ox = 0; ox < outWidth; ox++)
            {
                int x0 = (int)(ox * scale), x1 = Math.Min(width, Math.Max(x0 + 1, (int)Math.Ceiling((ox + 1) * scale)));
                int y0 = (int)(oy * scale), y1 = Math.Min(height, Math.Max(y0 + 1, (int)Math.Ceiling((oy + 1) * scale)));
                double r = 0, g = 0, b = 0, a = 0;
                var n = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var at = (y * width + x) * 4;
                        double alpha = rgba[at + 3];
                        r += rgba[at] * alpha; g += rgba[at + 1] * alpha; b += rgba[at + 2] * alpha; a += alpha; n++;
                    }
                }

                var to = (oy * outWidth + ox) * 4;
                if (a > 0) { result[to] = (byte)Math.Round(r / a); result[to + 1] = (byte)Math.Round(g / a); result[to + 2] = (byte)Math.Round(b / a); }
                result[to + 3] = (byte)Math.Round(a / n);
            }
        }

        return (result, outWidth, outHeight);
    }
}
