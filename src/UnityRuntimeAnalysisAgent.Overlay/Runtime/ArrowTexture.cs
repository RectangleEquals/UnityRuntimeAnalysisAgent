using System;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Overlay;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>
/// The overlay's arrow: an anti-aliased dart drawn into a texture (white, tinted by the renderer), pointing into the
/// screen from its edge. Generated, so it doesn't depend on a font having the ➤ glyph; one texture per edge, so no
/// renderer has to rotate it.
/// </summary>
public static class ArrowTexture
{
    // The dart pointing right, in unit coordinates (x right, y down): tip, lower barb, notch, upper barb.
    private static readonly (double X, double Y)[] Dart = { (0.86, 0.5), (0.22, 0.86), (0.4, 0.5), (0.22, 0.14) };

    /// <summary>The dart's coverage (0–1) at a pixel, with 4×4 supersampling, for a dart pointing into the screen from <paramref name="edge"/>.</summary>
    public static double Coverage(int x, int y, int size, OverlayEdge edge)
    {
        var inside = 0;
        for (var sy = 0; sy < 4; sy++)
        {
            for (var sx = 0; sx < 4; sx++)
            {
                var u = (x + (sx + 0.5) / 4) / size;
                var v = (y + (sy + 0.5) / 4) / size;
                // Rotate the sample back into the right-pointing dart's frame.
                var (px, py) = edge switch
                {
                    OverlayEdge.Right => (1 - u, v),
                    OverlayEdge.Top => (v, u),
                    OverlayEdge.Bottom => (1 - v, u),
                    _ => (u, v),
                };
                if (Inside(px, py))
                {
                    inside++;
                }
            }
        }

        return inside / 16.0;
    }

    /// <summary>A white-on-transparent arrow texture (point filtered, clamped) for an edge.</summary>
    public static Texture2D Create(int size, OverlayEdge edge)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "UnityRuntimeAnalysisAgent arrow " + edge,
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Texture rows go bottom-up.
                var alpha = (byte)Math.Round(Coverage(x, y, size, edge) * 255);
                pixels[(size - 1 - y) * size + x] = new Color32(255, 255, 255, alpha);
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        return texture;
    }

    private static bool Inside(double x, double y)
    {
        var inside = false;
        for (int i = 0, j = Dart.Length - 1; i < Dart.Length; j = i++)
        {
            var (xi, yi) = Dart[i];
            var (xj, yj) = Dart[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
