using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Overlay;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>What was typed since the last <see cref="KeyboardCapture.Take"/>.</summary>
public sealed class TypedInput
{
    /// <summary>
    /// Characters, with <c>\b</c> for Backspace, <c>\n</c> for Enter and <see cref="EditKeys"/> for the arrows, Home,
    /// End and Delete.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Ctrl+V (Cmd+V on macOS) was pressed.</summary>
    public bool Paste { get; set; }

    /// <summary>Ctrl+C was pressed.</summary>
    public bool Copy { get; set; }

    /// <summary>Ctrl+X was pressed.</summary>
    public bool Cut { get; set; }

    /// <summary>Escape was pressed.</summary>
    public bool Cancel { get; set; }

    /// <summary>A mouse button went down (wherever the pointer was).</summary>
    public bool MousePressed { get; set; }

    /// <summary>The mouse wheel's movement (positive = up, away from the user).</summary>
    public float Wheel { get; set; }
}

/// <summary>
/// While the overlay takes typing (<see cref="Active"/>), keeps the keyboard from the game and collects what's typed. It
/// works on whatever reads the keyboard, game-agnostically, from Unity 2018.1 on:
/// <list type="bullet">
/// <item>Unity's Input Manager: a step at the very start of each frame's Update (inserted into the player loop) reads the
/// typed characters, then calls <c>Input.ResetInputAxes</c>, so every key, button and axis reads as released to the
/// game's scripts for the rest of the frame. No patching: the engine's own key queries are tiny wrappers the runtime
/// inlines into the game's code, where a patch never runs.</item>
/// <item>The Input System package: keyboard state events are marked handled (the package's documented way to stop an
/// event), so no key changes state and no action fires; characters come from the keyboard's text input.</item>
/// <item>Windows (<c>auto</c>): a low-level keyboard hook, installed only while typing and only acting while the game's
/// window is in front, for games that read the keyboard outside Unity (some input middleware reads raw input itself).
/// A key it holds back is never seen by anything in the game; Alt and Windows-key combinations pass, so Alt+Tab
/// works.</item>
/// </list>
/// </summary>
public sealed class KeyboardCapture : IDisposable
{
    private readonly string _mode;
    private readonly IAgentLogger _log;
    private readonly object _gate = new();
    private readonly TypedInput _pending = new();
    private float _wheel;
    private readonly StringBuilder _text = new();
    private readonly LegacySource? _legacy;
    private readonly InputSystemSource? _inputSystem;
    private WindowsHook? _hook;
    private PlayerLoopStep? _step;
    private bool _active;

    /// <summary>Creates it for <c>Overlay.KeyboardCapture</c> (auto, unity or off).</summary>
    public KeyboardCapture(string mode, IAgentLogger log)
    {
        _mode = mode;
        _log = log;
        if (mode == "off")
        {
            return;
        }

        _legacy = LegacySource.TryCreate();
        _inputSystem = InputSystemSource.TryCreate(log);
    }

    /// <summary>Which ways are in use (for the overlay's status and tests).</summary>
    public string Description
    {
        get
        {
            var parts = new List<string>();
            if (_legacy is not null)
            {
                parts.Add("Input Manager");
            }

            if (_inputSystem is not null)
            {
                parts.Add("Input System");
            }

            if (_mode == "auto" && WindowsHook.Supported)
            {
                parts.Add("Windows keyboard hook");
            }

            return parts.Count == 0 ? "none" : string.Join(", ", parts.ToArray());
        }
    }

    /// <summary>Whether the overlay is taking typing now. Switching it on starts holding the keyboard back.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (value == _active || _mode == "off")
            {
                return;
            }

            _active = value;
            if (value)
            {
                Start();
            }
            else
            {
                Stop();
            }
        }
    }

    /// <summary>What was typed since the last call.</summary>
    public TypedInput Take()
    {
        lock (_gate)
        {
            if (_hook is not null)
            {
                _hook.GameFocused = Application.isFocused; // read by the hook's thread
                _hook.Drain(this);
            }
            var result = new TypedInput
            {
                Text = _text.ToString(), Paste = _pending.Paste, Copy = _pending.Copy, Cut = _pending.Cut, Cancel = _pending.Cancel,
                MousePressed = _pending.MousePressed || (_legacy is null && _inputSystem?.MousePressed() == true),
                Wheel = _legacy is null ? PointerButtons.Wheel() : _wheel, // the Input System's mouse isn't held back
            };
            _wheel = 0;
            _text.Length = 0;
            _pending.Paste = _pending.Copy = _pending.Cut = _pending.Cancel = _pending.MousePressed = false;
            return result;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Active = false;
        _step?.Dispose();
        _step = null;
        _inputSystem?.Dispose();
    }

    // Typed text from one of the sources (the one in charge: the hook when it holds keys, else the Input Manager, else
    // the Input System).
    internal void Add(string text)
    {
        lock (_gate)
        {
            _text.Append(text);
        }
    }

    internal void Add(char c)
    {
        lock (_gate)
        {
            _text.Append(c);
        }
    }

    internal void Scroll(float wheel)
    {
        lock (_gate)
        {
            _wheel += wheel;
        }
    }

    internal void Shortcut(char key)
    {
        lock (_gate)
        {
            switch (key)
            {
                case 'V': _pending.Paste = true; break;
                case 'C': _pending.Copy = true; break;
                case 'X': _pending.Cut = true; break;
                case '\u001b': _pending.Cancel = true; break;
                case 'M': _pending.MousePressed = true; break;
            }
        }
    }

    private void Start()
    {
        if (_mode == "auto" && WindowsHook.Supported)
        {
            _hook ??= new WindowsHook(_log);
            if (!_hook.Install())
            {
                _hook = null;
            }
        }

        if (_legacy is not null && _step is null)
        {
            _step = PlayerLoopStep.TryInsert(EarlyUpdate, _log);
        }

        _inputSystem?.Start(this, textSource: _legacy is null);
    }

    private void Stop()
    {
        _hook?.Uninstall();
        _inputSystem?.Stop();
        Take(); // nothing typed now carries over to the next time
        if (Counts.Any(c => c > 0))
        {
            _log.Info($"Typing session: the Windows keyboard hook held back {Counts[0]} key(s), the Input Manager gave {Counts[1]}, the Input System {Counts[2]}.");
        }

        Array.Clear(Counts, 0, Counts.Length);
    }

    // What each source delivered in this typing session (hook, Input Manager, Input System), logged when it ends.
    internal readonly int[] Counts = new int[3];

    // Runs first in each frame's Update: read what the Input Manager saw, then make it see nothing for the game. Keys the
    // Windows hook held back never reach the Input Manager, so the two never type the same key.
    private void EarlyUpdate()
    {
        if (!_active || _legacy is null)
        {
            return;
        }

        _legacy.Read(this);
        if (!_legacy.MouseBusy())
        {
            _legacy.Reset(); // not while a mouse button is down: the reset would take the click from the overlay too
        }
    }

    /// <summary>Unity's Input Manager, by reflection (it's missing where a project switched it off).</summary>
    private sealed class LegacySource
    {
        private static readonly KeyCode[] Controls = { KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftCommand, KeyCode.RightCommand };
        private static readonly KeyCode[] Shifts = { KeyCode.LeftShift, KeyCode.RightShift };

        private static readonly (KeyCode Key, char Edit)[] EditKeyCodes =
        {
            (KeyCode.UpArrow, EditKeys.Up), (KeyCode.DownArrow, EditKeys.Down), (KeyCode.PageUp, EditKeys.PageUp), (KeyCode.PageDown, EditKeys.PageDown),
            (KeyCode.Delete, EditKeys.Delete),
        };

        private readonly PropertyInfo _inputString;
        private readonly MethodInfo _getKey;
        private readonly MethodInfo _getKeyDown;
        private readonly MethodInfo _reset;
        private readonly MethodInfo? _getKeyUp;
        private readonly PropertyInfo? _scroll;
        private bool _broken;

        private LegacySource(PropertyInfo inputString, MethodInfo getKey, MethodInfo getKeyDown, MethodInfo reset)
        {
            _inputString = inputString;
            _getKey = getKey;
            _getKeyDown = getKeyDown;
            _reset = reset;
            _getKeyUp = getKey.DeclaringType!.GetMethod("GetKeyUp", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(KeyCode) }, null);
            _scroll = inputString.DeclaringType!.GetProperty("mouseScrollDelta", BindingFlags.Public | BindingFlags.Static);
        }

        public static LegacySource? TryCreate()
        {
            var input = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule")
                ?? Type.GetType("UnityEngine.Input, UnityEngine.CoreModule") ?? Type.GetType("UnityEngine.Input, UnityEngine");
            var flags = BindingFlags.Public | BindingFlags.Static;
            var inputString = input?.GetProperty("inputString", flags);
            var getKey = input?.GetMethod("GetKey", flags, null, new[] { typeof(KeyCode) }, null);
            var getKeyDown = input?.GetMethod("GetKeyDown", flags, null, new[] { typeof(KeyCode) }, null);
            var reset = input?.GetMethod("ResetInputAxes", flags, null, Type.EmptyTypes, null);
            if (inputString is null || getKey is null || getKeyDown is null || reset is null)
            {
                return null;
            }

            try
            {
                inputString.GetValue(null, null);
            }
            catch (TargetInvocationException)
            {
                return null; // the project switched the Input Manager off
            }

            return new LegacySource(inputString, getKey, getKeyDown, reset);
        }

        public void Read(KeyboardCapture capture)
        {
            if (_broken)
            {
                return;
            }

            try
            {
                // Held modifiers: Input.ResetInputAxes (ours, every frame while typing) makes held keys read as released,
                // so on Windows they come from the OS's key state; elsewhere from the Input Manager.
                var (osShift, osControl) = WindowsHook.Supported ? WindowsHook.Modifiers() : (false, false);
                var control = osControl || Controls.Any(k => (bool)_getKey.Invoke(null, new object[] { k }));
                var shift = osShift || Shifts.Any(k => (bool)_getKey.Invoke(null, new object[] { k }));
                foreach (var (key, letter) in new[] { (KeyCode.V, 'V'), (KeyCode.C, 'C'), (KeyCode.X, 'X') })
                {
                    if (control && (bool)_getKeyDown.Invoke(null, new object[] { key }))
                    {
                        capture.Shortcut(letter);
                    }
                }

                if (control && (bool)_getKeyDown.Invoke(null, new object[] { KeyCode.A }))
                {
                    capture.Add(EditKeys.SelectAll);
                }

                if ((bool)_getKeyDown.Invoke(null, new object[] { KeyCode.Escape }))
                {
                    capture.Shortcut('\u001b');
                }

                // Caret moves (Shift extends the selection); Home and End: their line, or with Ctrl the whole text; the
                // arrows with Ctrl: by words.
                var moves = EditKeyCodes.Concat(new[]
                {
                    (KeyCode.LeftArrow, control ? EditKeys.WordLeft : EditKeys.Left),
                    (KeyCode.RightArrow, control ? EditKeys.WordRight : EditKeys.Right),
                    (KeyCode.Home, control ? EditKeys.DocumentStart : EditKeys.Home),
                    (KeyCode.End, control ? EditKeys.DocumentEnd : EditKeys.End),
                });
                foreach (var (key, edit) in moves)
                {
                    if ((bool)_getKeyDown.Invoke(null, new object[] { key }))
                    {
                        if (shift && edit != EditKeys.Delete)
                        {
                            capture.Add(EditKeys.Extend);
                        }

                        capture.Add(edit);
                    }
                }

                if ((bool)_getKeyDown.Invoke(null, new object[] { KeyCode.Mouse0 }) || (bool)_getKeyDown.Invoke(null, new object[] { KeyCode.Mouse1 }))
                {
                    capture.Shortcut('M');
                }

                if (Wheel() is var wheel && wheel != 0)
                {
                    capture.Scroll(wheel);
                }

                if (!control && _inputString.GetValue(null, null) is string typed && typed.Length > 0)
                {
                    // Enter sends; Shift+Enter is a line break.
                    capture.Counts[1] += typed.Length;
                    capture.Add(typed.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', shift ? EditKeys.NewLine : '\n'));
                }
            }
            catch (TargetInvocationException)
            {
                _broken = true;
            }
        }

        // The mouse wheel's movement this frame (read before the reset, which clears it).
        public float Wheel() => !_broken && _scroll?.GetValue(null, null) is Vector2 delta ? delta.y : 0;

        // Whether a mouse button is down or went down this frame, or the wheel moved: the reset would take that from the
        // overlay too (its buttons, its scrolling).
        public bool MouseBusy()
        {
            if (_broken)
            {
                return false;
            }

            try
            {
                // The release frame too: the reset would swallow the release, and the overlay would think the button still held.
                return Wheel() != 0 || new[] { KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2 }.Any(k =>
                    (bool)_getKey.Invoke(null, new object[] { k }) || (bool)_getKeyDown.Invoke(null, new object[] { k }) || (_getKeyUp?.Invoke(null, new object[] { k }) is true));
            }
            catch (TargetInvocationException)
            {
                _broken = true;
                return false;
            }
        }

        public void Reset()
        {
            if (_broken)
            {
                return;
            }

            try
            {
                _reset.Invoke(null, null);
            }
            catch (TargetInvocationException)
            {
                _broken = true;
            }
        }
    }

    /// <summary>
    /// A step at the start of the player loop's Update phase (after the engine read the frame's input, before any script
    /// runs). Unity 2018.1–2019.2 have the API under <c>UnityEngine.Experimental.LowLevel</c>, later versions under
    /// <c>UnityEngine.LowLevel</c>; both are found by reflection.
    /// </summary>
    private sealed class PlayerLoopStep : IDisposable
    {
        private readonly Type _loopType;
        private readonly Type _systemType;
        private bool _inserted;

        private PlayerLoopStep(Type loopType, Type systemType)
        {
            _loopType = loopType;
            _systemType = systemType;
        }

        public static PlayerLoopStep? TryInsert(Action update, IAgentLogger log)
        {
            var core = typeof(Application).Assembly;
            var loopType = core.GetType("UnityEngine.LowLevel.PlayerLoop") ?? core.GetType("UnityEngine.Experimental.LowLevel.PlayerLoop");
            var systemType = core.GetType("UnityEngine.LowLevel.PlayerLoopSystem") ?? core.GetType("UnityEngine.Experimental.LowLevel.PlayerLoopSystem");
            if (loopType is null || systemType is null)
            {
                log.Warning("The keyboard can't be kept from the game's Input Manager here (no player loop API).");
                return null;
            }

            var step = new PlayerLoopStep(loopType, systemType);
            try
            {
                step.Change(update);
                return step;
            }
            catch (Exception e)
            {
                log.Warning($"The keyboard can't be kept from the game's Input Manager here: {e.GetBaseException().Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (!_inserted)
            {
                return;
            }

            try
            {
                Change(null);
            }
            catch (Exception)
            {
                // the engine is shutting down
            }
        }

        // Inserts our step (update not null) or removes it, keeping every other system (the game's and other mods').
        private void Change(Action? update)
        {
            var flags = BindingFlags.Public | BindingFlags.Static;
            var get = _loopType.GetMethod("GetCurrentPlayerLoop", flags) ?? _loopType.GetMethod("GetDefaultPlayerLoop", flags)!;
            var set = _loopType.GetMethod("SetPlayerLoop", flags)!;
            var typeField = _systemType.GetField("type")!;
            var listField = _systemType.GetField("subSystemList")!;
            var delegateField = _systemType.GetField("updateDelegate")!;
            var root = get.Invoke(null, null)!; // boxed: field writes below change this copy
            var phases = (Array)listField.GetValue(root)!;
            for (var i = 0; i < phases.Length; i++)
            {
                var phase = phases.GetValue(i)!;
                if ((typeField.GetValue(phase) as Type)?.Name != "Update")
                {
                    continue;
                }

                var systems = ((Array?)listField.GetValue(phase) ?? Array.CreateInstance(_systemType, 0)).Cast<object>()
                    .Where(s => typeField.GetValue(s) as Type != typeof(KeyboardCapture)).ToList();
                if (update is not null)
                {
                    var ours = Activator.CreateInstance(_systemType)!;
                    typeField.SetValue(ours, typeof(KeyboardCapture));
                    delegateField.SetValue(ours, Delegate.CreateDelegate(delegateField.FieldType, update.Target, update.Method));
                    systems.Insert(0, ours);
                }

                var array = Array.CreateInstance(_systemType, systems.Count);
                for (var j = 0; j < systems.Count; j++)
                {
                    array.SetValue(systems[j], j);
                }

                listField.SetValue(phase, array);
                phases.SetValue(phase, i);
                listField.SetValue(root, phases);
                set.Invoke(null, new[] { root });
                _inserted = update is not null;
                return;
            }

            throw new InvalidOperationException("the player loop has no Update phase");
        }
    }

    /// <summary>
    /// The Input System package, by reflection (namespace <c>UnityEngine.InputSystem</c>, or
    /// <c>UnityEngine.Experimental.Input</c> in the 2018–2019 previews).
    /// </summary>
    private sealed class InputSystemSource : IDisposable
    {
        private readonly Type _keyboardType;
        private readonly PropertyInfo _current;
        private readonly Delegate _listener;
        private readonly EventInfo? _event;
        private readonly MethodInfo? _add;
        private readonly MethodInfo? _remove;
        private readonly MethodInfo? _resetDevice;
        private readonly IAgentLogger _log;
        private readonly Dictionary<string, bool> _down = new(StringComparer.Ordinal);
        private KeyboardCapture? _capture;
        private bool _text;
        private object? _keyboard;
        private Action<char>? _onText;

        private InputSystemSource(Type keyboardType, PropertyInfo current, Delegate listener, EventInfo? evt, MethodInfo? add, MethodInfo? remove, MethodInfo? resetDevice, IAgentLogger log)
        {
            _keyboardType = keyboardType;
            _current = current;
            _listener = listener;
            _event = evt;
            _add = add;
            _remove = remove;
            _resetDevice = resetDevice;
            _log = log;
        }

        public static InputSystemSource? TryCreate(IAgentLogger log)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.InputSystem");
            if (assembly is null)
            {
                return null;
            }

            try
            {
                var ns = assembly.GetType("UnityEngine.InputSystem.InputSystem") is not null ? "UnityEngine.InputSystem" : "UnityEngine.Experimental.Input";
                var system = assembly.GetType(ns + ".InputSystem");
                var keyboard = assembly.GetType(ns + ".Keyboard");
                var current = keyboard?.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                if (system is null || keyboard is null || current is null)
                {
                    return null;
                }

                // onEvent is an event of Action<InputEventPtr, InputDevice> (or Action<InputEventPtr> in early
                // previews), or, from Input System 1.4, a property of type InputEventListener with + and - operators.
                var evt = system.GetEvent("onEvent", BindingFlags.Public | BindingFlags.Static);
                var property = evt is null ? system.GetProperty("onEvent", BindingFlags.Public | BindingFlags.Static) : null;
                var handlerType = evt?.EventHandlerType
                    ?? property?.PropertyType.GetMethod("op_Addition")?.GetParameters()[1].ParameterType;
                if (handlerType is null)
                {
                    return null;
                }

                var invoke = handlerType.GetMethod("Invoke")!;
                var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                var target = Expression.Constant(new Box());
                var call = Expression.Call(target, typeof(Box).GetMethod(nameof(Box.Invoke))!,
                    Expression.Convert(parameters[0], typeof(object)),
                    parameters.Length > 1 ? Expression.Convert(parameters[1], typeof(object)) : Expression.Constant(null, typeof(object)));
                var listener = Expression.Lambda(handlerType, call, parameters).Compile();
                var resetDevice = system.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "ResetDevice" && m.GetParameters().Length >= 1);
                var result = new InputSystemSource(keyboard, current, listener, evt,
                    property?.PropertyType.GetMethod("op_Addition"), property?.PropertyType.GetMethod("op_Subtraction"), resetDevice, log);
                ((Box)target.Value!).Source = result;
                return result;
            }
            catch (Exception e)
            {
                log.Warning($"The keyboard can't be kept from the game's Input System here: {e.GetBaseException().Message}");
                return null;
            }
        }

        public void Start(KeyboardCapture capture, bool textSource)
        {
            _capture = capture;
            _text = textSource;
            _down.Clear();
            try
            {
                if (_event is not null)
                {
                    _event.AddEventHandler(null, _listener);
                }
                else
                {
                    _add?.Invoke(null, new object?[] { null, _listener });
                }

                _keyboard = _current.GetValue(null, null);
                if (_keyboard is not null)
                {
                    // Keys held as typing starts would stay down for the game until typing ends: release them now.
                    if (_resetDevice is not null)
                    {
                        var args = _resetDevice.GetParameters().Select(p => p.ParameterType.IsInstanceOfType(_keyboard) ? _keyboard : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                        _resetDevice.Invoke(null, args);
                    }

                    if (_text)
                    {
                        _onText = c =>
                        {
                            if (!char.IsControl(c) && _capture is not null)
                            {
                                _capture.Counts[2]++;
                                _capture.Add(c);
                            }
                        };
                        _keyboardType.GetEvent("onTextInput")?.AddEventHandler(_keyboard, _onText);
                    }
                }
            }
            catch (Exception e)
            {
                _log.Warning($"The keyboard can't be kept from the game's Input System: {e.GetBaseException().Message}");
            }
        }

        public void Stop()
        {
            try
            {
                if (_event is not null)
                {
                    _event.RemoveEventHandler(null, _listener);
                }
                else
                {
                    _remove?.Invoke(null, new object?[] { null, _listener });
                }

                if (_keyboard is not null && _onText is not null)
                {
                    _keyboardType.GetEvent("onTextInput")?.RemoveEventHandler(_keyboard, _onText);
                }
            }
            catch (Exception)
            {
                // the package is shutting down
            }

            _onText = null;
            _keyboard = null;
            _capture = null;
        }

        public void Dispose() => Stop();

        // Whether a mouse button went down this frame (the Input System's mouse isn't held back).
        public bool MousePressed()
        {
            try
            {
                var mouseType = _keyboardType.Assembly.GetType(_keyboardType.Namespace + ".Mouse");
                var mouse = mouseType?.GetProperty("current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                foreach (var name in new[] { "leftButton", "rightButton" })
                {
                    var button = mouse?.GetType().GetProperty(name)?.GetValue(mouse, null);
                    if (button?.GetType().GetProperty("wasPressedThisFrame")?.GetValue(button, null) is true)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // no mouse
            }

            return false;
        }

        // One input event: a keyboard state change is read (Enter, Backspace, Escape and the shortcuts, when the Input
        // System is where typing comes from), then dropped before the keyboard or any action sees it.
        private void OnEvent(object eventPtr, object? device)
        {
            if (_capture is null || !_keyboardType.IsInstanceOfType(device ?? _keyboard))
            {
                return;
            }

            var ptr = eventPtr.GetType();
            var kind = ptr.GetProperty("type")?.GetValue(eventPtr, null)?.ToString();
            if (kind is not ("STAT" or "DLTA"))
            {
                return; // text and other events go on (characters reach onTextInput)
            }

            if (_text)
            {
                var keyboard = device ?? _keyboard!;
                var control = Pressed(keyboard, eventPtr, "ctrlKey") || Pressed(keyboard, eventPtr, "leftMetaKey") || Pressed(keyboard, eventPtr, "rightMetaKey");
                foreach (var (name, letter) in new[] { ("vKey", 'V'), ("cKey", 'C'), ("xKey", 'X') })
                {
                    if (control && Edge(keyboard, eventPtr, name))
                    {
                        _capture.Shortcut(letter);
                    }
                }

                if (control && Edge(keyboard, eventPtr, "aKey"))
                {
                    _capture.Add(EditKeys.SelectAll);
                }

                if (Edge(keyboard, eventPtr, "escapeKey"))
                {
                    _capture.Shortcut('\u001b');
                }

                if (Edge(keyboard, eventPtr, "enterKey") || Edge(keyboard, eventPtr, "numpadEnterKey"))
                {
                    _capture.Add(Pressed(keyboard, eventPtr, "shiftKey") ? EditKeys.NewLine : '\n'); // Shift+Enter: a line break
                }

                if (Edge(keyboard, eventPtr, "backspaceKey"))
                {
                    _capture.Add('\b');
                }

                foreach (var (name, edit) in new[]
                {
                    ("leftArrowKey", control ? EditKeys.WordLeft : EditKeys.Left), ("rightArrowKey", control ? EditKeys.WordRight : EditKeys.Right),
                    ("upArrowKey", EditKeys.Up), ("downArrowKey", EditKeys.Down),
                    ("pageUpKey", EditKeys.PageUp), ("pageDownKey", EditKeys.PageDown), ("deleteKey", EditKeys.Delete),
                    ("homeKey", control ? EditKeys.DocumentStart : EditKeys.Home), ("endKey", control ? EditKeys.DocumentEnd : EditKeys.End),
                })
                {
                    if (Edge(keyboard, eventPtr, name))
                    {
                        if (edit != EditKeys.Delete && Pressed(keyboard, eventPtr, "shiftKey"))
                        {
                            _capture.Add(EditKeys.Extend); // Shift: the move extends the selection
                        }

                        _capture.Add(edit);
                    }
                }
            }

            ptr.GetProperty("handled")?.SetValue(eventPtr, true, null);
        }

        // Whether a key went down in this event (it wasn't down in the last one we read).
        private bool Edge(object keyboard, object eventPtr, string key)
        {
            var now = Pressed(keyboard, eventPtr, key);
            var before = _down.TryGetValue(key, out var was) && was;
            _down[key] = now;
            return now && !before;
        }

        private static bool Pressed(object keyboard, object eventPtr, string key)
        {
            var control = keyboard.GetType().GetProperty(key)?.GetValue(keyboard, null);
            var read = control?.GetType().GetMethods().FirstOrDefault(m => m.Name == "ReadValueFromEvent" && m.GetParameters().Length == 2);
            if (read is null)
            {
                return false;
            }

            var args = new[] { eventPtr, null };
            return read.Invoke(control, args) is true && args[1] is float value && value >= 0.5f;
        }

        // The compiled listener's target (the listener is built before the source exists).
        private sealed class Box
        {
            public InputSystemSource? Source;

            public void Invoke(object eventPtr, object? device) => Source?.OnEvent(eventPtr, device);
        }
    }

    /// <summary>
    /// A Windows low-level keyboard hook (<c>WH_KEYBOARD_LL</c>): it sees each key before Windows turns it into the
    /// messages and raw input every input library reads, and holds it back while the game's window is in front. It runs
    /// on a thread of its own with its own message loop: Windows silently removes a low-level hook whose callback is late,
    /// and the game's main thread is late whenever a frame is slow.
    /// </summary>
    private sealed class WindowsHook
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x100;
        private const int WmSysKeyDown = 0x104;
        private const uint LlkhfAltDown = 0x20;
        private const uint LlkhfInjected = 0x10;
        private const uint VkPacket = 0xE7;

        // Held for the hook's lifetime: the native side only has a function pointer to it.
        private static LowLevelKeyboardProc? s_proc;
        private static WindowsHook? s_current;

        private readonly IAgentLogger _log;
        private readonly int _processId = Process.GetCurrentProcess().Id;
        private readonly List<(uint Vk, uint Scan, bool Shift, bool Control, bool Alt, bool Caps)> _keys = new();
        private readonly HashSet<uint> _held = new();
        private IntPtr _handle;
        private Thread? _thread;
        private uint _threadId;
        private int _passedBehind;
        private int _seen;
        private int _passedModifiers;
        private int _injected;
        private uint _behindProcess;

        public WindowsHook(IAgentLogger log)
        {
            _log = log;
        }

        /// <summary>Whether the game's window has the keyboard (Unity's focus), set from the main thread every frame.</summary>
        public volatile bool GameFocused;

        private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

        public static bool Supported => Application.platform == RuntimePlatform.WindowsPlayer;

        /// <summary>Whether Shift and Ctrl are held now, from the OS (unaffected by Unity's input resets).</summary>
        public static (bool Shift, bool Control) Modifiers() =>
            ((GetAsyncKeyState(0x10) & 0x8000) != 0, (GetAsyncKeyState(0x11) & 0x8000) != 0);

        // Installs the hook on its own thread for one typing session (a fresh hook each time).
        public bool Install()
        {
            Uninstall();
            try
            {
                s_proc ??= Proc;
                s_current = this;
                var ready = new ManualResetEvent(false);
                var error = 0;
                _thread = new Thread(() =>
                {
                    _threadId = GetCurrentThreadId();
                    _handle = SetWindowsHookEx(WhKeyboardLl, s_proc, GetModuleHandle(null), 0);
                    error = _handle == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                    ready.Set();
                    if (_handle == IntPtr.Zero)
                    {
                        return;
                    }

                    // The hook's callbacks run in this loop; Uninstall posts WM_QUIT to end it.
                    while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
                    {
                    }

                    UnhookWindowsHookEx(_handle);
                    _handle = IntPtr.Zero;
                })
                {
                    IsBackground = true,
                    Name = "UnityRuntimeAnalysisAgent keyboard hook",
                };
                _thread.Start();
                ready.WaitOne(2000);
                if (_handle == IntPtr.Zero)
                {
                    _log.Warning($"The Windows keyboard hook couldn't be installed (error {error}); Unity's input is still held back.");
                    _thread = null;
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                _log.Warning($"The Windows keyboard hook isn't available: {e.Message}");
                return false;
            }
        }

        public void Uninstall()
        {
            if (_thread is not null)
            {
                PostThreadMessage(_threadId, 0x12, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
                _thread.Join(1000);
                _thread = null;
            }

            if (_seen > 0)
            {
                _log.Info($"Windows keyboard hook: {_seen} key event(s) seen; passed on: {_passedModifiers} modifier/Alt/Windows-key, {_passedBehind} while the game didn't have the keyboard (foreground process {_behindProcess}, ours {_processId}); {_injected} held back were injected (remote desktop or automation).");
            }

            _seen = _passedModifiers = _passedBehind = _injected = 0;
        }

        // Turns the keys held back since the last call into text and shortcuts, with the keyboard layout in use.
        public void Drain(KeyboardCapture capture)
        {
            List<(uint Vk, uint Scan, bool Shift, bool Control, bool Alt, bool Caps)> keys;
            lock (_keys)
            {
                if (_keys.Count == 0)
                {
                    return;
                }

                keys = new List<(uint, uint, bool, bool, bool, bool)>(_keys);
                _keys.Clear();
            }

            capture.Counts[0] += keys.Count;
            var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
            var state = new byte[256];
            var buffer = new StringBuilder(8);
            foreach (var key in keys)
            {
                if (key.Vk == VkPacket)
                {
                    var typed = (char)key.Scan;
                    capture.Add(typed == '\r' ? (key.Shift ? EditKeys.NewLine : '\n') : typed);
                    continue;
                }

                char? move = key.Vk switch
                {
                    0x25 => key.Control ? EditKeys.WordLeft : EditKeys.Left,
                    0x27 => key.Control ? EditKeys.WordRight : EditKeys.Right,
                    0x26 => EditKeys.Up,
                    0x28 => EditKeys.Down,
                    0x21 => EditKeys.PageUp,
                    0x22 => EditKeys.PageDown,
                    0x24 => key.Control ? EditKeys.DocumentStart : EditKeys.Home,
                    0x23 => key.Control ? EditKeys.DocumentEnd : EditKeys.End,
                    _ => null,
                };
                if (move is { } m)
                {
                    if (key.Shift)
                    {
                        capture.Add(EditKeys.Extend); // Shift: the move extends the selection
                    }

                    capture.Add(m);
                    continue;
                }

                switch (key.Vk)
                {
                    case 0x0D: capture.Add(key.Shift ? EditKeys.NewLine : '\n'); continue; // Enter sends, Shift+Enter: a line break
                    case 0x08: capture.Add('\b'); continue; // Backspace
                    case 0x1B: capture.Shortcut('\u001b'); continue; // Escape
                    case 0x2E: capture.Add(EditKeys.Delete); continue;
                }

                if (key.Control && !key.Alt)
                {
                    if (key.Vk is 'V' or 'C' or 'X')
                    {
                        capture.Shortcut((char)key.Vk);
                    }
                    else if (key.Vk == 'A')
                    {
                        capture.Add(EditKeys.SelectAll);
                    }

                    continue;
                }

                Array.Clear(state, 0, state.Length);
                state[0x10] = state[0xA0] = (byte)(key.Shift ? 0x80 : 0);
                state[0x11] = state[0xA2] = (byte)(key.Control ? 0x80 : 0); // Ctrl+Alt = AltGr
                state[0x12] = state[0xA5] = (byte)(key.Alt ? 0x80 : 0);
                state[0x14] = (byte)(key.Caps ? 0x01 : 0);
                buffer.Length = 0;
                var count = ToUnicodeEx(key.Vk, key.Scan, state, buffer, buffer.Capacity, 0x4, layout); // 0x4: keep the keyboard's dead-key state
                if (count > 0)
                {
                    capture.Add(buffer.ToString(0, count));
                }
            }
        }

        private static IntPtr Proc(int code, IntPtr message, IntPtr data)
        {
            var self = s_current;
            if (code < 0 || self is null || self._handle == IntPtr.Zero)
            {
                return CallNextHookEx(IntPtr.Zero, code, message, data);
            }

            self._seen++;
            var vk = (uint)Marshal.ReadInt32(data);
            var scan = (uint)Marshal.ReadInt32(data, 4);
            var flags = (uint)Marshal.ReadInt32(data, 8);
            var down = (int)message is WmKeyDown or WmSysKeyDown;
            if (!down)
            {
                // A key-up is held back only when its key-down was: a key pressed before typing started still gets released.
                return self._held.Remove(vk) ? (IntPtr)1 : CallNextHookEx(IntPtr.Zero, code, message, data);
            }

            var control = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            var alt = (flags & LlkhfAltDown) != 0;
            var windows = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
            if (IsModifier(vk) || windows || (alt && !control))
            {
                self._passedModifiers++;
                return CallNextHookEx(IntPtr.Zero, code, message, data); // modifiers, Alt+Tab, Windows-key shortcuts
            }

            if (!self.GameFocused)
            {
                // The game's window doesn't have the keyboard (Unity's own focus): the key goes where Windows sends it.
                GetWindowThreadProcessId(GetForegroundWindow(), out self._behindProcess);
                self._passedBehind++;
                return CallNextHookEx(IntPtr.Zero, code, message, data);
            }

            if ((flags & LlkhfInjected) != 0)
            {
                self._injected++; // remote-desktop and automation input (held back like any other key)
            }

            lock (self._keys)
            {
                self._keys.Add((vk, scan, (GetAsyncKeyState(0x10) & 0x8000) != 0, control, alt, (GetKeyState(0x14) & 1) != 0));
            }

            self._held.Add(vk);
            return (IntPtr)1;
        }

        private static bool IsModifier(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x14 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, IntPtr module, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out Msg message, IntPtr window, uint first, uint last);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Window;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? name);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int key);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint threadId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder buffer, int size, uint flags, IntPtr layout);
    }
}
