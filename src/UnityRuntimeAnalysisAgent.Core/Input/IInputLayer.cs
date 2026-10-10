using System.Collections.Generic;

namespace UnityRuntimeAnalysisAgent.Core.Input;

/// <summary>
/// A way into the game's input (Rewired actions, Input System devices, Input Manager queries, an XInput wrapper, a
/// per-game recipe), implemented by the Unity bindings. A layer merges the session's <see cref="VirtualInput"/> into what
/// the game reads while attached, and tells real input apart from virtual input, so the user's own input can take over.
/// </summary>
public interface IInputLayer
{
    /// <summary>The protocol name: <c>rewired</c>, <c>inputSystem</c>, <c>inputManager</c>, <c>xinput</c> or <c>recipe</c>.</summary>
    string Id { get; }

    /// <summary>Whether it applies to this game, its version and why (re-evaluated on each call).</summary>
    InputLayerStatus Status { get; }

    /// <summary>The real devices this layer's input stack sees.</summary>
    IReadOnlyList<(string Kind, string Name)> Devices();

    /// <summary>The game's actions this layer knows (name, kind: button/axis/axis2d, player or null).</summary>
    IReadOnlyList<(string Name, string Kind, int? Player)> Actions();

    /// <summary>Named axes (the Input Manager's), when known.</summary>
    IReadOnlyList<string> Axes();

    /// <summary>Starts merging the virtual input into what the game reads (main thread).</summary>
    void Attach(VirtualInput input);

    /// <summary>Stops merging; the game reads only real input again (main thread).</summary>
    void Detach();

    /// <summary>Whether the user used real input this frame (a key, a button, the mouse moving past a few pixels, a
    /// stick past its deadzone), read without the virtual merge (main thread).</summary>
    bool RealInput();

    /// <summary>Whether the user holds a chord: all of the keys (KeyCode names) or all of the pad buttons (main thread).</summary>
    bool RealChord(IReadOnlyList<string> keys, IReadOnlyList<string> padButtons);
}

/// <summary>Whether an input layer applies, and what it can drive.</summary>
public sealed class InputLayerStatus
{
    /// <summary>Creates a status.</summary>
    public InputLayerStatus(bool available, string? version, IReadOnlyList<string> drives, string reason)
    {
        Available = available;
        Version = version;
        Drives = drives;
        Reason = reason;
    }

    /// <summary>Whether it applies to this game.</summary>
    public bool Available { get; }

    /// <summary>The input stack's version, if it reports one.</summary>
    public string? Version { get; }

    /// <summary>What it drives: keyboard, mouse, gamepad, actions.</summary>
    public IReadOnlyList<string> Drives { get; }

    /// <summary>What was found (or missing).</summary>
    public string Reason { get; }
}
