using System;
using System.Collections.Generic;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Overlay.Runtime;
using UnityRuntimeAnalysisAgent.Overlay.Views;

namespace UnityRuntimeAnalysisAgent.Overlay.Ugui;

/// <summary>
/// Generated sprites for the uGUI renderer: rounded rectangles (filled, and border rings) as nine-sliced white sprites,
/// tinted by the theme; one per radius and border width, made once.
/// </summary>
public sealed class UguiSprites : IDisposable
{
    private readonly Dictionary<(int Radius, int Border), Sprite> _cache = new();

    /// <summary>The fill (border 0) or ring sprite for a corner radius and border width, in pixels.</summary>
    public Sprite Get(int radius, int border)
    {
        radius = Math.Max(0, Math.Min(radius, 64));
        border = Math.Max(0, Math.Min(border, 16));
        if (_cache.TryGetValue((radius, border), out var sprite))
        {
            return sprite;
        }

        var size = radius * 2 + 4;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var alpha = Coverage(x, y, size, radius, border);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)Math.Round(alpha * 255));
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        var slice = radius + 2;
        sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(slice, slice, slice, slice));
        sprite.hideFlags = HideFlags.HideAndDontSave;
        _cache[(radius, border)] = sprite;
        return sprite;
    }

    /// <summary>How much of a pixel a rounded rectangle (or its ring) covers, 4×4 supersampled.</summary>
    public static double Coverage(int x, int y, int size, int radius, int border)
    {
        var inside = 0;
        for (var sy = 0; sy < 4; sy++)
        {
            for (var sx = 0; sx < 4; sx++)
            {
                var px = x + (sx + 0.5) / 4;
                var py = y + (sy + 0.5) / 4;
                var outer = Distance(px, py, size, radius);
                if (outer <= 0 && (border == 0 || outer > -border))
                {
                    inside++;
                }
            }
        }

        return inside / 16.0;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var sprite in _cache.Values)
        {
            UnityEngine.Object.Destroy(sprite.texture);
            UnityEngine.Object.Destroy(sprite);
        }

        _cache.Clear();
    }

    // Signed distance to a rounded rectangle filling the texture (negative inside).
    private static double Distance(double x, double y, int size, int radius)
    {
        var half = size / 2.0;
        var qx = Math.Abs(x - half) - (half - radius);
        var qy = Math.Abs(y - half) - (half - radius);
        var outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0));
        return outside + Math.Min(Math.Max(qx, qy), 0) - radius;
    }
}

/// <summary>Fonts for the uGUI renderer (from the bundle, else a Windows font) and text measurement with Unity's TextGenerator.</summary>
public sealed class UguiFonts : ITextMeasurer
{
    private readonly LoadedBundle? _bundle;
    private readonly bool _retro;
    private readonly Dictionary<string, Font> _fonts = new(StringComparer.Ordinal);
    private readonly TextGenerator _generator = new();
    private Font? _fallback;

    /// <summary>Creates the fonts.</summary>
    public UguiFonts(LoadedBundle? bundle, bool retroFonts)
    {
        _bundle = bundle;
        _retro = retroFonts;
    }

    /// <summary>The font and integer size for a role and requested size (the canvas scale is applied by the canvas).</summary>
    public (Font Font, int Size) For(FontRole role, double size)
    {
        var (name, drawSize) = FontChoice.For(role, size, _retro && _bundle is not null, 1);
        if (!_fonts.TryGetValue(name, out var font))
        {
            font = _bundle?.Font(name) ?? Fallback();
            if (_retro && name.StartsWith("ark-pixel", StringComparison.Ordinal))
            {
                Crisp(font);
            }

            _fonts[name] = font;
        }

        return (font, Math.Max(1, (int)Math.Round(drawSize)));
    }

    /// <inheritdoc />
    public (double Width, double Height) Measure(string text, FontRole font, double size, double maxWidth, bool noWrap)
    {
        var (f, s) = For(font, size);
        var settings = new TextGenerationSettings
        {
            font = f,
            fontSize = s,
            lineSpacing = 1,
            scaleFactor = 1,
            richText = false,
            color = Color.white,
            pivot = Vector2.zero,
            generationExtents = new Vector2(noWrap || double.IsInfinity(maxWidth) ? 100000 : (float)Math.Max(1, maxWidth), 100000),
            horizontalOverflow = noWrap || double.IsInfinity(maxWidth) ? HorizontalWrapMode.Overflow : HorizontalWrapMode.Wrap,
            verticalOverflow = VerticalWrapMode.Overflow,
            textAnchor = TextAnchor.UpperLeft,
            updateBounds = false,
            generateOutOfBounds = true,
        };
        var width = Math.Ceiling(_generator.GetPreferredWidth(text, settings));
        var height = Math.Ceiling(_generator.GetPreferredHeight(text, settings));
        return (noWrap || double.IsInfinity(maxWidth) ? width : Math.Min(width, Math.Ceiling(maxWidth)), height);
    }

    // Pixel fonts are imported without glyph padding: sampled bilinearly, neighbouring glyphs bleed into each other. A
    // dynamic font gets a new atlas texture whenever it grows, so the filter is set again on every rebuild.
    private static readonly HashSet<Font> PixelFonts = new();
    private static bool s_watching;

    private static void Crisp(Font font)
    {
        PixelFonts.Add(font);
        if (!s_watching)
        {
            s_watching = true;
            Font.textureRebuilt += rebuilt =>
            {
                if (rebuilt != null && PixelFonts.Contains(rebuilt) && rebuilt.material != null && rebuilt.material.mainTexture is { } texture)
                {
                    texture.filterMode = FilterMode.Point;
                }
            };
        }

        if (font.material != null && font.material.mainTexture is { } atlas)
        {
            atlas.filterMode = FilterMode.Point;
        }
    }

    private Font Fallback() => _fallback ??= Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Arial", "Liberation Sans" }, 14);
}
