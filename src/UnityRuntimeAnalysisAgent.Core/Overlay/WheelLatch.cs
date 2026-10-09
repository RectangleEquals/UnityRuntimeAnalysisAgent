namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>
/// Which of the overlay's scrollers a mouse-wheel event goes to: a wheel event with the pointer outside the text boxes
/// sets a flag that makes the boxes ignore the wheel until it rests for <see cref="Gap"/>, so scrolling a panel never
/// switches to a box that slides under the pointer. With the pointer on a box and no flag, the box gets the event.
/// </summary>
public sealed class WheelLatch
{
    /// <summary>The default <see cref="Gap"/>, in seconds.</summary>
    public const double DefaultGap = 0.5;

    /// <summary>How long the wheel must rest before a new gesture can belong to something else, in seconds (the
    /// <c>Overlay.WheelLatch</c> setting).</summary>
    public double Gap { get; set; } = DefaultGap;

    private double _last = double.NegativeInfinity;
    private bool _ignoreBoxes;

    /// <summary>Whether the latest wheel event went to a text box.</summary>
    public bool OnBox { get; private set; }

    /// <summary>A wheel event at a time, over a text box or not: whether the box gets it.</summary>
    public bool Claim(bool overBox, double now)
    {
        if (now - _last > Gap)
        {
            _ignoreBoxes = false; // the wheel rested: a new gesture
        }

        _last = now;
        if (!overBox)
        {
            _ignoreBoxes = true;
        }

        OnBox = overBox && !_ignoreBoxes;
        return OnBox;
    }
}
