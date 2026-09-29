using System;
using System.Collections.Generic;
using UnityRuntimeAnalysisAgent.Core.Abstractions;

namespace UnityRuntimeAnalysisAgent.Core.Diagnostics;

/// <summary>
/// CPU image operations for captures, on any thread. Images are RGBA with rows bottom to top (Unity's order); the
/// rectangles taken here are in pixels with the origin at the top left, like screen and UI rectangles.
/// </summary>
public static class ImageOps
{
    /// <summary>The part of <paramref name="image"/> inside the rectangle (clamped to the image); null if nothing is left.</summary>
    public static ImagePixels? Crop(ImagePixels image, int x, int top, int width, int height)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, top);
        var x1 = Math.Min(image.Width, x + width);
        var y1 = Math.Min(image.Height, top + height);
        if (x1 <= x0 || y1 <= y0)
        {
            return null;
        }

        int w = x1 - x0, h = y1 - y0;
        var rgba = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            // Output row `row` from the bottom is the source row (top y1 - 1 - row) counted from the top.
            var sourceRow = image.Height - y1 + row;
            Buffer.BlockCopy(image.Rgba, ((sourceRow * image.Width) + x0) * 4, rgba, row * w * 4, w * 4);
        }

        return new ImagePixels(w, h, rgba);
    }

    /// <summary>Shrinks the image to fit the limits (aspect kept, area-averaged); returns it unchanged if it fits.</summary>
    public static ImagePixels Fit(ImagePixels image, int? maxWidth, int? maxHeight)
    {
        var scale = Math.Min(1.0, Math.Min(maxWidth is { } mw ? (double)mw / image.Width : 1.0, maxHeight is { } mh ? (double)mh / image.Height : 1.0));
        if (scale >= 1.0)
        {
            return image;
        }

        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        return Resize(image, w, h);
    }

    /// <summary>Downscales with a box filter (each output pixel averages the source area it covers).</summary>
    public static ImagePixels Resize(ImagePixels image, int width, int height)
    {
        // Separable: horizontal pass into floats, then vertical.
        var sx = (double)image.Width / width;
        var sy = (double)image.Height / height;
        var temp = new float[width * image.Height * 4];
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Accumulate(image.Rgba, (y * image.Width) * 4, 4, x * sx, (x + 1) * sx, image.Width, temp, ((y * width) + x) * 4);
            }
        }

        var rgba = new byte[width * height * 4];
        var column = new float[image.Height * 4];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < image.Height; y++)
            {
                Array.Copy(temp, ((y * width) + x) * 4, column, y * 4, 4);
            }

            for (var y = 0; y < height; y++)
            {
                var start = y * sy;
                var end = (y + 1) * sy;
                var sum = new float[4];
                double weight = 0;
                for (var sy0 = (int)start; sy0 < Math.Min(image.Height, (int)Math.Ceiling(end)); sy0++)
                {
                    var w = Math.Min(end, sy0 + 1) - Math.Max(start, sy0);
                    for (var c = 0; c < 4; c++)
                    {
                        sum[c] += (float)(column[(sy0 * 4) + c] * w);
                    }

                    weight += w;
                }

                for (var c = 0; c < 4; c++)
                {
                    rgba[(((y * width) + x) * 4) + c] = (byte)Math.Max(0, Math.Min(255, Math.Round(sum[c] / weight)));
                }
            }
        }

        return new ImagePixels(width, height, rgba);
    }

    /// <summary>A rectangle outline (<paramref name="thickness"/> pixels, inside the rectangle).</summary>
    public static void DrawRect(ImagePixels image, int x, int top, int width, int height, uint color, int thickness = 2)
    {
        Fill(image, x, top, width, thickness, color);
        Fill(image, x, top + height - thickness, width, thickness, color);
        Fill(image, x, top, thickness, height, color);
        Fill(image, x + width - thickness, top, thickness, height, color);
    }

    /// <summary>The size of a label drawn by <see cref="DrawLabel"/>.</summary>
    public static (int Width, int Height) LabelSize(string text, int scale = 2) => ((text.Length * 6 * scale) - scale + 4, (7 * scale) + 4);

    /// <summary>A label: <paramref name="text"/> in a small bitmap font (letters, digits, a little punctuation) on a filled
    /// box whose top-left corner is at (<paramref name="x"/>, <paramref name="top"/>), kept inside the image. Returns the
    /// box size.</summary>
    public static (int Width, int Height) DrawLabel(ImagePixels image, int x, int top, string text, uint background, uint foreground, int scale = 2)
    {
        const int pad = 2;
        var glyphs = text.ToUpperInvariant();
        var width = (glyphs.Length * 6 * scale) - scale + (pad * 2);
        var height = (7 * scale) + (pad * 2);
        x = Math.Max(0, Math.Min(x, image.Width - width));
        top = Math.Max(0, Math.Min(top, image.Height - height));
        Fill(image, x, top, width, height, background);
        for (var i = 0; i < glyphs.Length; i++)
        {
            var glyph = Glyph(glyphs[i]);
            for (var gy = 0; gy < 7; gy++)
            {
                for (var gx = 0; gx < 5; gx++)
                {
                    if (glyph[gy][gx] == '#')
                    {
                        Fill(image, x + pad + (((i * 6) + gx) * scale), top + pad + (gy * scale), scale, scale, foreground);
                    }
                }
            }
        }

        return (width, height);
    }

    /// <summary>Fills a rectangle (clamped to the image).</summary>
    public static void Fill(ImagePixels image, int x, int top, int width, int height, uint color)
    {
        byte r = (byte)(color >> 24), g = (byte)(color >> 16), b = (byte)(color >> 8), a = (byte)color;
        var x0 = Math.Max(0, x);
        var x1 = Math.Min(image.Width, x + width);
        for (var yy = Math.Max(0, top); yy < Math.Min(image.Height, top + height); yy++)
        {
            var row = image.Height - 1 - yy;
            for (var xx = x0; xx < x1; xx++)
            {
                var i = ((row * image.Width) + xx) * 4;
                image.Rgba[i] = r;
                image.Rgba[i + 1] = g;
                image.Rgba[i + 2] = b;
                image.Rgba[i + 3] = a;
            }
        }
    }

    private static void Accumulate(byte[] source, int rowOffset, int stride, double start, double end, int count, float[] target, int targetOffset)
    {
        var sum = new double[4];
        double weight = 0;
        for (var i = (int)start; i < Math.Min(count, (int)Math.Ceiling(end)); i++)
        {
            var w = Math.Min(end, i + 1) - Math.Max(start, i);
            for (var c = 0; c < 4; c++)
            {
                sum[c] += source[rowOffset + (i * stride) + c] * w;
            }

            weight += w;
        }

        for (var c = 0; c < 4; c++)
        {
            target[targetOffset + c] = (float)(sum[c] / weight);
        }
    }

    private static string[] Glyph(char c) => Font.TryGetValue(c, out var glyph) ? glyph : Font['?'];

    // 5×7 glyphs, generated for this purpose (no font asset involved).
    private static readonly Dictionary<char, string[]> Font = new()
    {
        [' '] = new[] { "     ", "     ", "     ", "     ", "     ", "     ", "     " },
        ['0'] = new[] { " ### ", "#   #", "#  ##", "# # #", "##  #", "#   #", " ### " },
        ['1'] = new[] { "  #  ", " ##  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### " },
        ['2'] = new[] { " ### ", "#   #", "    #", "   # ", "  #  ", " #   ", "#####" },
        ['3'] = new[] { "#####", "   # ", "  #  ", "   # ", "    #", "#   #", " ### " },
        ['4'] = new[] { "   # ", "  ## ", " # # ", "#  # ", "#####", "   # ", "   # " },
        ['5'] = new[] { "#####", "#    ", "#### ", "    #", "    #", "#   #", " ### " },
        ['6'] = new[] { "  ## ", " #   ", "#    ", "#### ", "#   #", "#   #", " ### " },
        ['7'] = new[] { "#####", "    #", "   # ", "  #  ", " #   ", " #   ", " #   " },
        ['8'] = new[] { " ### ", "#   #", "#   #", " ### ", "#   #", "#   #", " ### " },
        ['9'] = new[] { " ### ", "#   #", "#   #", " ####", "    #", "   # ", " ##  " },
        ['A'] = new[] { " ### ", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" },
        ['B'] = new[] { "#### ", "#   #", "#   #", "#### ", "#   #", "#   #", "#### " },
        ['C'] = new[] { " ### ", "#   #", "#    ", "#    ", "#    ", "#   #", " ### " },
        ['D'] = new[] { "#### ", "#   #", "#   #", "#   #", "#   #", "#   #", "#### " },
        ['E'] = new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#####" },
        ['F'] = new[] { "#####", "#    ", "#    ", "#### ", "#    ", "#    ", "#    " },
        ['G'] = new[] { " ### ", "#   #", "#    ", "# ###", "#   #", "#   #", " ####" },
        ['H'] = new[] { "#   #", "#   #", "#   #", "#####", "#   #", "#   #", "#   #" },
        ['I'] = new[] { " ### ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", " ### " },
        ['J'] = new[] { "  ###", "   # ", "   # ", "   # ", "   # ", "#  # ", " ##  " },
        ['K'] = new[] { "#   #", "#  # ", "# #  ", "##   ", "# #  ", "#  # ", "#   #" },
        ['L'] = new[] { "#    ", "#    ", "#    ", "#    ", "#    ", "#    ", "#####" },
        ['M'] = new[] { "#   #", "## ##", "# # #", "# # #", "#   #", "#   #", "#   #" },
        ['N'] = new[] { "#   #", "#   #", "##  #", "# # #", "#  ##", "#   #", "#   #" },
        ['O'] = new[] { " ### ", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " },
        ['P'] = new[] { "#### ", "#   #", "#   #", "#### ", "#    ", "#    ", "#    " },
        ['Q'] = new[] { " ### ", "#   #", "#   #", "#   #", "# # #", "#  # ", " ## #" },
        ['R'] = new[] { "#### ", "#   #", "#   #", "#### ", "# #  ", "#  # ", "#   #" },
        ['S'] = new[] { " ####", "#    ", "#    ", " ### ", "    #", "    #", "#### " },
        ['T'] = new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  ", "  #  ", "  #  " },
        ['U'] = new[] { "#   #", "#   #", "#   #", "#   #", "#   #", "#   #", " ### " },
        ['V'] = new[] { "#   #", "#   #", "#   #", "#   #", "#   #", " # # ", "  #  " },
        ['W'] = new[] { "#   #", "#   #", "#   #", "# # #", "# # #", "# # #", " # # " },
        ['X'] = new[] { "#   #", "#   #", " # # ", "  #  ", " # # ", "#   #", "#   #" },
        ['Y'] = new[] { "#   #", "#   #", " # # ", "  #  ", "  #  ", "  #  ", "  #  " },
        ['Z'] = new[] { "#####", "    #", "   # ", "  #  ", " #   ", "#    ", "#####" },
        ['-'] = new[] { "     ", "     ", "     ", "#####", "     ", "     ", "     " },
        ['.'] = new[] { "     ", "     ", "     ", "     ", "     ", " ##  ", " ##  " },
        [':'] = new[] { "     ", " ##  ", " ##  ", "     ", " ##  ", " ##  ", "     " },
        ['#'] = new[] { " # # ", " # # ", "#####", " # # ", "#####", " # # ", " # # " },
        ['?'] = new[] { " ### ", "#   #", "    #", "   # ", "  #  ", "     ", "  #  " },
    };
}
