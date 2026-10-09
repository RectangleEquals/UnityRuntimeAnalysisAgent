namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>
/// When a view node's tooltip shows (every renderer uses one): after the pointer rests on the node for
/// <see cref="Delay"/>, until it leaves the node (or a press, or a redraw that replaces the node).
/// </summary>
public sealed class TooltipTimer
{
    /// <summary>How long the pointer rests on a node before its tooltip shows, in seconds.</summary>
    public const double Delay = 0.5;

    private object? _target;
    private string? _text;
    private double _since;

    /// <summary>The pointer entered a node with a tooltip (<paramref name="target"/> identifies the node).</summary>
    public void Enter(object target, string text, double now)
    {
        if (Equals(_target, target))
        {
            return;
        }

        _target = target;
        _text = text;
        _since = now;
    }

    /// <summary>The pointer left a node: its tooltip goes.</summary>
    public void Leave(object target)
    {
        if (Equals(_target, target))
        {
            Clear();
        }
    }

    /// <summary>No tooltip (a press, or the nodes were redrawn).</summary>
    public void Clear()
    {
        _target = null;
        _text = null;
    }

    /// <summary>The node whose tooltip it is, or null.</summary>
    public object? Target => _target;

    /// <summary>The tooltip to show now, or null (none, or the pointer hasn't rested long enough).</summary>
    public string? Shown(double now) => _text is not null && now - _since >= Delay ? _text : null;
}
