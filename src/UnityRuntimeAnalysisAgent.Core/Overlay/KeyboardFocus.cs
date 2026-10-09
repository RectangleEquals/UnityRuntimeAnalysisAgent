using System;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>
/// Which text box has the overlay's keyboard, if any (one for the whole overlay). Typed keys go to <see cref="Focused"/>;
/// while it's set the game doesn't see the keyboard. A click in a box focuses it, a click anywhere else blurs it.
/// </summary>
public sealed class KeyboardFocus
{
    /// <summary>The text box that takes the keyboard, or null (the game has it).</summary>
    public TextBox? Focused { get; private set; }

    /// <summary>Changes whenever the focus moves (renderers redraw on it).</summary>
    public int Version { get; private set; }

    /// <summary>Raised when the focus moves (to a box, or to none).</summary>
    public event Action<TextBox?>? Changed;

    /// <summary>Gives a box the keyboard.</summary>
    public void Focus(TextBox box) => Set(box);

    /// <summary>Gives the keyboard back to the game.</summary>
    public void Blur() => Set(null);

    /// <summary>A box going away: blurs it if it had the keyboard.</summary>
    public void Release(TextBox box)
    {
        if (ReferenceEquals(Focused, box))
        {
            Set(null);
        }
    }

    private void Set(TextBox? box)
    {
        if (ReferenceEquals(Focused, box))
        {
            return;
        }

        Focused = box;
        Version++;
        Changed?.Invoke(box);
    }
}
