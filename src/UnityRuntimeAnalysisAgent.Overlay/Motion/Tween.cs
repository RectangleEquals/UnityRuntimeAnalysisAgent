using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Overlay.Motion;

/// <summary>An easing curve.</summary>
public enum Easing
{
    /// <summary>Constant speed.</summary>
    Linear,

    /// <summary>Starts fast, slows down (panels sliding in).</summary>
    OutCubic,

    /// <summary>Starts slow, speeds up (sliding out).</summary>
    InCubic,

    /// <summary>Slow at both ends.</summary>
    InOutCubic,

    /// <summary>Overshoots a little and settles (toasts popping in).</summary>
    OutBack,
}

/// <summary>
/// Animations of numbers (positions, opacity, scale) over unscaled realtime, keyed by name so starting one again
/// retargets it from where it is. The same for every renderer, and unaffected by the game's time scale or pause.
/// </summary>
public sealed class TweenSet
{
    private readonly Dictionary<string, Tween> _tweens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);

    /// <summary>Whether anything is still moving (renderers skip updates when not).</summary>
    public bool Animating => _tweens.Count > 0;

    /// <summary>Starts (or retargets) an animation from the current value.</summary>
    public void To(string key, double target, double durationSeconds, Easing easing, double now)
    {
        var from = Value(key, target);
        if (durationSeconds <= 0 || Math.Abs(from - target) < 1e-9)
        {
            _tweens.Remove(key);
            _values[key] = target;
            return;
        }

        _tweens[key] = new Tween(from, target, now, durationSeconds, easing);
    }

    /// <summary>Sets a value at once.</summary>
    public void Set(string key, double value)
    {
        _tweens.Remove(key);
        _values[key] = value;
    }

    /// <summary>The current value (after <see cref="Tick"/>), or the fallback if never set.</summary>
    public double Value(string key, double fallback = 0) => _values.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>Advances every animation to <paramref name="now"/> (unscaled realtime, seconds).</summary>
    public void Tick(double now)
    {
        foreach (var pair in _tweens.ToList())
        {
            var t = Math.Min(1, Math.Max(0, (now - pair.Value.Start) / pair.Value.Duration));
            _values[pair.Key] = pair.Value.From + (pair.Value.To - pair.Value.From) * Ease(pair.Value.Easing, t);
            if (t >= 1)
            {
                _tweens.Remove(pair.Key);
            }
        }
    }

    /// <summary>An easing curve at t (0–1).</summary>
    public static double Ease(Easing easing, double t)
    {
        switch (easing)
        {
            case Easing.OutCubic:
                return 1 - Math.Pow(1 - t, 3);
            case Easing.InCubic:
                return t * t * t;
            case Easing.InOutCubic:
                return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            case Easing.OutBack:
                const double c1 = 1.70158;
                const double c3 = c1 + 1;
                return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
            default:
                return t;
        }
    }

    private readonly struct Tween
    {
        public Tween(double from, double to, double start, double duration, Easing easing)
        {
            From = from;
            To = to;
            Start = start;
            Duration = duration;
            Easing = easing;
        }

        public double From { get; }

        public double To { get; }

        public double Start { get; }

        public double Duration { get; }

        public Easing Easing { get; }
    }
}
