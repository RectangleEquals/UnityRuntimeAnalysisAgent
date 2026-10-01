using System;
using System.Collections.Generic;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Overlay.Views;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>
/// Animated effects (the theme's presets): each is drawn with the bundle's effects shader into a small render texture
/// that renderers show as a background. Updated at a capped rate (<see cref="FramesPerSecond"/>), only for effects shown
/// in the last frame, and not at all with <c>Overlay.Effects = off</c>; <c>reduced</c> freezes them.
/// </summary>
public sealed class EffectsDriver : IDisposable
{
    /// <summary>Updates per second while visible.</summary>
    public const double FramesPerSecond = 15;

    private const string ShaderName = "Hidden/UnityRuntimeAnalysisAgent/Overlay/Effects";

    private readonly Theme _theme;
    private readonly Material? _material;
    private readonly string _mode;
    private readonly Dictionary<string, RenderTexture> _textures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private double _next;

    /// <summary>Creates the driver (no effects without the bundle's shader, or with effects off).</summary>
    public EffectsDriver(Theme theme, LoadedBundle? bundle, string mode)
    {
        _theme = theme;
        _mode = mode;
        if (mode != "off" && bundle?.Shader(ShaderName) is { isSupported: true } shader)
        {
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }
    }

    /// <summary>Draws in this frame: how many effect textures are updated per second (for the performance counters).</summary>
    public long Draws { get; private set; }

    /// <summary>The texture for a preset (made on first use), or null when effects aren't available.</summary>
    public Texture? Texture(string preset)
    {
        if (_material is null || !_theme.Effects.TryGetValue(preset, out var effect))
        {
            return null;
        }

        _used.Add(preset);
        if (!_textures.TryGetValue(preset, out var texture))
        {
            texture = new RenderTexture(128, 48, 0) { hideFlags = HideFlags.HideAndDontSave, filterMode = effect.Pixels > 0 ? FilterMode.Point : FilterMode.Bilinear };
            _textures[preset] = texture;
            Draw(preset, effect, texture);
        }

        return texture;
    }

    /// <summary>Every frame: redraws the effects used since the last update, at most <see cref="FramesPerSecond"/> times a second.</summary>
    public void Tick(double now)
    {
        if (_material is null || _mode == "reduced" || now < _next)
        {
            return;
        }

        _next = now + 1 / FramesPerSecond;
        foreach (var preset in _used)
        {
            if (_textures.TryGetValue(preset, out var texture) && _theme.Effects.TryGetValue(preset, out var effect))
            {
                Draw(preset, effect, texture);
            }
        }

        _used.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var texture in _textures.Values)
        {
            texture.Release();
            UnityEngine.Object.Destroy(texture);
        }

        _textures.Clear();
        if (_material != null)
        {
            UnityEngine.Object.Destroy(_material);
        }
    }

    private void Draw(string preset, EffectPreset effect, RenderTexture texture)
    {
        _material!.SetColor("_ColorA", Rgba(effect.ColorA));
        _material.SetColor("_ColorB", Rgba(effect.ColorB));
        _material.SetFloat("_Pixels", (float)effect.Pixels);
        _material.SetFloat("_Speed", (float)effect.Speed);
        var pass = effect.Kind switch
        {
            "fire" => 1,
            "water" => 2,
            _ => 0,
        };
        Graphics.Blit(null, texture, _material, pass);
        Draws++;
    }

    /// <summary>0xRRGGBBAA as a Unity colour.</summary>
    public static Color Rgba(uint rgba) => new(((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);
}
