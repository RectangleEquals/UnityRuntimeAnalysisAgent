using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityRuntimeAnalysisAgent.Core.Input;

/// <summary>
/// The virtual input of an input session: keys (by <c>UnityEngine.KeyCode</c> name), mouse, gamepad and action values that
/// the input layers merge into what the game reads. Held state stays until changed; taps press an input for some frames
/// or milliseconds. The state advances once per frame (<see cref="Advance"/>, called lazily by the first query of a
/// frame, whatever the script order), so "pressed this frame" and "released this frame" work like Unity's own queries.
/// Thread-safe: clients set it from any thread, the game reads it on the main thread.
/// </summary>
public sealed class VirtualInput
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    private readonly object _gate = new();
    private readonly HashSet<string> _keys = new(Names);
    private readonly HashSet<string> _mouseButtons = new(Names);
    private readonly HashSet<string> _padButtons = new(Names);
    private readonly Dictionary<string, ActionValue> _actions = new(Names); // "player/name"
    private readonly List<Tap> _taps = new();
    private Frame _now = Frame.Empty;
    private Frame _before = Frame.Empty;
    private long _frame = -1;
    private (double X, double Y)? _position;
    private (double X, double Y) _delta;
    private (double X, double Y) _wheel;
    private long _deltaFrame = -1; // the frame the delta and wheel were set for
    private (double X, double Y) _leftStick;
    private (double X, double Y) _rightStick;
    private double _leftTrigger;
    private double _rightTrigger;

    /// <summary>Whether anything is held, tapping or moving (layers merge only then).</summary>
    public bool Active { get; private set; }

    /// <summary>The frame the state was last advanced to.</summary>
    public long FrameNumber
    {
        get
        {
            lock (_gate)
            {
                return _frame;
            }
        }
    }

    /// <summary>Replaces the held keys.</summary>
    public void SetKeys(IEnumerable<string> keys) => Change(() => Replace(_keys, keys));

    /// <summary>Replaces the held mouse buttons, and sets position, this frame's movement and wheel when given.</summary>
    public void SetMouse(IEnumerable<string>? buttons, (double X, double Y)? position, (double X, double Y)? delta, (double X, double Y)? wheel)
    {
        Change(() =>
        {
            if (buttons is not null)
            {
                Replace(_mouseButtons, buttons);
            }

            if (position is { } p)
            {
                _position = p;
            }

            if (delta is not null || wheel is not null)
            {
                _delta = delta ?? _delta;
                _wheel = wheel ?? _wheel;
                _deltaFrame = _frame + 1; // seen by the next frame, then cleared
            }
        });
    }

    /// <summary>Replaces the held gamepad buttons, and sets sticks and triggers when given.</summary>
    public void SetGamepad(IEnumerable<string>? buttons, (double X, double Y)? left, (double X, double Y)? right, double? leftTrigger, double? rightTrigger)
    {
        Change(() =>
        {
            if (buttons is not null)
            {
                Replace(_padButtons, buttons);
            }

            _leftStick = left is { } l ? Clamp(l) : _leftStick;
            _rightStick = right is { } r ? Clamp(r) : _rightStick;
            _leftTrigger = leftTrigger is { } lt ? Math.Max(0, Math.Min(1, lt)) : _leftTrigger;
            _rightTrigger = rightTrigger is { } rt ? Math.Max(0, Math.Min(1, rt)) : _rightTrigger;
        });
    }

    /// <summary>Replaces the held action values.</summary>
    public void SetActions(IEnumerable<(int Player, string Name, ActionValue Value)> actions)
    {
        Change(() =>
        {
            _actions.Clear();
            foreach (var (player, name, value) in actions)
            {
                _actions[ActionKey(player, name)] = value;
            }
        });
    }

    /// <summary>
    /// Presses one input from the next frame until <paramref name="holdFrames"/> frames have passed and, when given,
    /// <paramref name="holdSeconds"/> have passed too. The returned tap reports its down and up frames.
    /// </summary>
    public Tap Press(InputKind kind, string name, int player, int holdFrames, double holdSeconds)
    {
        var tap = new Tap(kind, name, player, Math.Max(1, holdFrames), Math.Max(0, holdSeconds));
        Change(() => _taps.Add(tap));
        return tap;
    }

    /// <summary>Releases everything: held state, movement and taps (finished at once).</summary>
    public void ReleaseAll()
    {
        Change(() =>
        {
            _keys.Clear();
            _mouseButtons.Clear();
            _padButtons.Clear();
            _actions.Clear();
            _delta = _wheel = default;
            _leftStick = _rightStick = default;
            _leftTrigger = _rightTrigger = 0;
            foreach (var tap in _taps)
            {
                tap.Finish(_frame);
            }

            _taps.Clear();
        });
    }

    /// <summary>
    /// Moves the state to a frame: taps start and end, this frame's held set is computed (the previous one kept for
    /// edges), and movement set for an earlier frame is cleared. Repeated calls for the same frame do nothing.
    /// </summary>
    public void Advance(long frame, double time)
    {
        lock (_gate)
        {
            if (frame <= _frame)
            {
                return;
            }

            _frame = frame;
            foreach (var tap in _taps)
            {
                tap.Step(frame, time);
            }

            _taps.RemoveAll(t => t.Done);
            if (_deltaFrame < frame)
            {
                _delta = _wheel = default;
            }

            _before = _now;
            _now = Snapshot();
            Active = _now.Any || _delta != default || _wheel != default || _leftStick != default || _rightStick != default
                || _leftTrigger > 0 || _rightTrigger > 0 || _actions.Count > 0 || _taps.Count > 0;
        }
    }

    /// <summary>Whether an input is held this frame.</summary>
    public bool Held(InputKind kind, string name, int player = 0)
    {
        lock (_gate)
        {
            return _now.Has(kind, kind == InputKind.Action ? ActionKey(player, name) : name);
        }
    }

    /// <summary>Whether an input went down this frame.</summary>
    public bool Down(InputKind kind, string name, int player = 0)
    {
        lock (_gate)
        {
            var key = kind == InputKind.Action ? ActionKey(player, name) : name;
            return _now.Has(kind, key) && !_before.Has(kind, key);
        }
    }

    /// <summary>Whether an input came up this frame.</summary>
    public bool Up(InputKind kind, string name, int player = 0)
    {
        lock (_gate)
        {
            var key = kind == InputKind.Action ? ActionKey(player, name) : name;
            return !_now.Has(kind, key) && _before.Has(kind, key);
        }
    }

    /// <summary>The virtual pointer position (screen pixels, origin top left), if set.</summary>
    public (double X, double Y)? MousePosition
    {
        get
        {
            lock (_gate)
            {
                return _position;
            }
        }
    }

    /// <summary>This frame's virtual mouse movement and wheel.</summary>
    public ((double X, double Y) Delta, (double X, double Y) Wheel) MouseMotion
    {
        get
        {
            lock (_gate)
            {
                return (_delta, _wheel);
            }
        }
    }

    /// <summary>The gamepad's sticks and triggers.</summary>
    public ((double X, double Y) Left, (double X, double Y) Right, double LeftTrigger, double RightTrigger) GamepadAxes
    {
        get
        {
            lock (_gate)
            {
                return (_leftStick, _rightStick, _leftTrigger, _rightTrigger);
            }
        }
    }

    /// <summary>An action's held value (a tapped button action reads as pressed), or null.</summary>
    public ActionValue? Action(string name, int player = 0)
    {
        lock (_gate)
        {
            var key = ActionKey(player, name);
            if (_actions.TryGetValue(key, out var value))
            {
                return value;
            }

            return _now.Has(InputKind.Action, key) ? ActionValue.Button(true) : null;
        }
    }

    /// <summary>The keys held this frame (for layers that answer "any key").</summary>
    public IReadOnlyCollection<string> KeysHeld
    {
        get
        {
            lock (_gate)
            {
                return _now.Keys.ToArray();
            }
        }
    }

    internal static string ActionKey(int player, string name) => player.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + name;

    private static (double X, double Y) Clamp((double X, double Y) v) => (Math.Max(-1, Math.Min(1, v.X)), Math.Max(-1, Math.Min(1, v.Y)));

    private static void Replace(HashSet<string> set, IEnumerable<string> values)
    {
        set.Clear();
        foreach (var value in values)
        {
            set.Add(value);
        }
    }

    private void Change(Action change)
    {
        lock (_gate)
        {
            change();
            Active = true; // until the next Advance recomputes it
        }
    }

    // The held set of the current frame: held state plus the taps that are down.
    private Frame Snapshot()
    {
        var frame = new Frame(new HashSet<string>(_keys, Names), new HashSet<string>(_mouseButtons, Names), new HashSet<string>(_padButtons, Names), new HashSet<string>(Names));
        foreach (var pair in _actions)
        {
            if (pair.Value.IsPressed)
            {
                frame.Actions.Add(pair.Key);
            }
        }

        foreach (var tap in _taps.Where(t => t.IsDown))
        {
            var set = tap.Kind switch
            {
                InputKind.Key => frame.Keys,
                InputKind.MouseButton => frame.Mouse,
                InputKind.GamepadButton => frame.Pad,
                _ => frame.Actions,
            };
            set.Add(tap.Kind == InputKind.Action ? ActionKey(tap.Player, tap.Name) : tap.Name);
        }

        return frame;
    }

    private sealed class Frame
    {
        public static readonly Frame Empty = new(new HashSet<string>(Names), new HashSet<string>(Names), new HashSet<string>(Names), new HashSet<string>(Names));

        public Frame(HashSet<string> keys, HashSet<string> mouse, HashSet<string> pad, HashSet<string> actions)
        {
            Keys = keys;
            Mouse = mouse;
            Pad = pad;
            Actions = actions;
        }

        public HashSet<string> Keys { get; }

        public HashSet<string> Mouse { get; }

        public HashSet<string> Pad { get; }

        public HashSet<string> Actions { get; }

        public bool Any => Keys.Count + Mouse.Count + Pad.Count + Actions.Count > 0;

        public bool Has(InputKind kind, string name) => kind switch
        {
            InputKind.Key => Keys.Contains(name),
            InputKind.MouseButton => Mouse.Contains(name),
            InputKind.GamepadButton => Pad.Contains(name),
            _ => Actions.Contains(name),
        };
    }
}

/// <summary>What kind of input a name refers to.</summary>
public enum InputKind
{
    /// <summary>A keyboard key (<c>UnityEngine.KeyCode</c> name).</summary>
    Key,

    /// <summary>A mouse button: left, right, middle, back, forward.</summary>
    MouseButton,

    /// <summary>A gamepad button by position: south, east, …</summary>
    GamepadButton,

    /// <summary>A game action (Rewired, Input System, recipe).</summary>
    Action,
}

/// <summary>An action's value: a button (pressed or not), an axis, or a 2D axis.</summary>
public readonly struct ActionValue
{
    private ActionValue(bool? button, double axis, (double X, double Y)? axis2D)
    {
        ButtonValue = button;
        Axis = axis;
        Axis2D = axis2D;
    }

    /// <summary>The button state, when the value is a button.</summary>
    public bool? ButtonValue { get; }

    /// <summary>The axis value (a button reads 1 or 0; a 2D axis its x).</summary>
    public double Axis { get; }

    /// <summary>The 2D value, when the value is a 2D axis.</summary>
    public (double X, double Y)? Axis2D { get; }

    /// <summary>Whether it reads as pressed (a button that's down, or an axis away from zero).</summary>
    public bool IsPressed => ButtonValue ?? Math.Abs(Axis) > 0.5;

    /// <summary>A button value.</summary>
    public static ActionValue Button(bool pressed) => new(pressed, pressed ? 1 : 0, null);

    /// <summary>An axis value.</summary>
    public static ActionValue Of(double value) => new(null, value, null);

    /// <summary>A 2D axis value.</summary>
    public static ActionValue Of(double x, double y) => new(null, x, (x, y));
}

/// <summary>One press of an input (<see cref="VirtualInput.Press"/>): down from the next frame, up when held long enough.</summary>
public sealed class Tap
{
    private long _start = -1;
    private double _startTime;

    internal Tap(InputKind kind, string name, int player, int holdFrames, double holdSeconds)
    {
        Kind = kind;
        Name = name;
        Player = player;
        HoldFrames = holdFrames;
        HoldSeconds = holdSeconds;
    }

    /// <summary>What is pressed.</summary>
    public InputKind Kind { get; }

    /// <summary>Its name.</summary>
    public string Name { get; }

    /// <summary>The player (actions).</summary>
    public int Player { get; }

    /// <summary>Frames it stays down (at least one).</summary>
    public int HoldFrames { get; }

    /// <summary>Seconds it stays down (at least; 0 = frames only).</summary>
    public double HoldSeconds { get; }

    /// <summary>The first frame it was down, or -1.</summary>
    public long DownFrame { get; private set; } = -1;

    /// <summary>The first frame it was up again, or -1.</summary>
    public long UpFrame { get; private set; } = -1;

    /// <summary>Whether it's down in the current frame.</summary>
    public bool IsDown => DownFrame >= 0 && UpFrame < 0;

    /// <summary>Whether it has been released.</summary>
    public bool Done => UpFrame >= 0;

    internal void Step(long frame, double time)
    {
        if (Done)
        {
            return;
        }

        if (_start < 0)
        {
            _start = frame;
            _startTime = time;
            DownFrame = frame;
            return;
        }

        if (frame - _start >= HoldFrames && time - _startTime >= HoldSeconds)
        {
            UpFrame = frame;
        }
    }

    internal void Finish(long frame)
    {
        if (!Done)
        {
            DownFrame = DownFrame < 0 ? frame : DownFrame;
            UpFrame = Math.Max(frame, DownFrame);
        }
    }
}
