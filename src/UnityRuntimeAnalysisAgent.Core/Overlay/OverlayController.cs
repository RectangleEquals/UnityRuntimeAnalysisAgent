using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Overlay;

/// <summary>
/// <see cref="IOverlayQueries"/> over the agent's dispatcher: requests with source <c>overlay</c>, answered on the main
/// thread (the overlay's state is only touched there). View reads are left out of the Activity feed.
/// </summary>
public sealed class DispatcherQueries : IOverlayQueries
{
    private readonly Dispatcher _dispatcher;
    private readonly MainThreadPump _pump;
    private long _next;

    /// <summary>Creates the adapter.</summary>
    public DispatcherQueries(Dispatcher dispatcher, MainThreadPump pump)
    {
        _dispatcher = dispatcher;
        _pump = pump;
    }

    /// <inheritdoc />
    public void Read(string method, JsonObject parameters, Action<JsonValue?, ProtocolError?> done) => Send(method, parameters, done, unrecorded: true);

    /// <inheritdoc />
    public void Act(string method, JsonObject parameters, Action<JsonValue?, ProtocolError?> done) => Send(method, parameters, done, unrecorded: false);

    private void Send(string method, JsonObject parameters, Action<JsonValue?, ProtocolError?> done, bool unrecorded)
    {
        var id = "overlay-" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        var context = new RequestContext(id, method, parameters, null, "overlay", "overlay", null, CancellationToken.None) { Unrecorded = unrecorded };
        _dispatcher.Dispatch(context, outcome =>
        {
            if (Thread.CurrentThread.ManagedThreadId == _pump.MainThreadId)
            {
                done(outcome.Result, outcome.Error);
                return;
            }

            _pump.Enqueue(new PumpWork(() =>
            {
                done(outcome.Result, outcome.Error);
                return null;
            }, _ => { }, _ => { }));
        });
    }
}

/// <summary>
/// The overlay's model layer in one place: state, toasts, prompts, highlights, the selection and its lock,
/// E-STOP, pick names and the tab view models. It has no UnityEngine types; a renderer draws it and feeds input back.
/// <see cref="Tick"/> runs every frame on the main thread with unscaled realtime.
/// </summary>
public sealed partial class OverlayController
{
    private readonly Func<object, long> _handleOf;
    private double _lastNow;

    /// <summary>Creates the controller.</summary>
    /// <param name="settings">The <c>Overlay.*</c> settings.</param>
    /// <param name="writer">Where changed settings are saved, if anywhere.</param>
    /// <param name="modes">The agent's mode (the Inspector lock follows it).</param>
    /// <param name="queries">In-process access to the services.</param>
    /// <param name="estop">E-STOP's steps.</param>
    /// <param name="log">The agent's log.</param>
    /// <param name="handleOf">Gives an object a handle (for the Inspector's reads).</param>
    public OverlayController(OverlaySettings settings, IConfigWriter? writer, ModeController modes, IOverlayQueries queries, EStopSteps estop,
        IAgentLogger log, Func<object, long> handleOf)
    {
        _handleOf = handleOf;
        _writer = writer;
        _modes = modes;
        Settings = settings;
        Model = new OverlayModel(settings, writer);
        Toasts = new ToastQueue(() => Settings.Toasts);
        Prompts = new PromptRegistry(Keyboard);
        Wheel.Gap = settings.WheelLatch;
        Highlights = new HighlightSet();
        Selection = new SelectionModel();
        Lock = new InspectorLock(modes, settings.InspectorStartsLocked);
        Picks = new PickNames();
        Queries = queries;
        EStop = new EStop(estop, log, () => Settings.EStopPauses, () => Settings.EStopDisconnects);
        Views = new OverlayViewModels(queries, Model, LocalState, () => Selection.Current is { Destroyed: false } s ? _handleOf(s.Target) : null)
        {
            Postprocess = Postprocess,
        };
    }

    /// <summary>The settings.</summary>
    public OverlaySettings Settings { get; }

    /// <summary>The state machine and docking.</summary>
    public OverlayModel Model { get; }

    /// <summary>The notifications.</summary>
    public ToastQueue Toasts { get; }

    /// <summary>Which text box has the overlay's keyboard.</summary>
    public KeyboardFocus Keyboard { get; } = new();

    /// <summary>Which scroller gets the mouse wheel (text boxes or the panels around them).</summary>
    public WheelLatch Wheel { get; } = new();

    /// <summary>The prompts waiting for the user.</summary>
    public PromptRegistry Prompts { get; }

    /// <summary>The drawn overlay's elements for clients that drive it, or null while no renderer draws it.</summary>
    public IOverlayAutomation? Automation { get; set; }

    /// <summary>The highlights on screen.</summary>
    public HighlightSet Highlights { get; }

    /// <summary>The Inspector's selection.</summary>
    public SelectionModel Selection { get; }

    /// <summary>The Inspector lock.</summary>
    public InspectorLock Lock { get; }

    /// <summary>Default names for picks.</summary>
    public PickNames Picks { get; }

    /// <summary>E-STOP.</summary>
    public EStop EStop { get; }

    /// <summary>The tab view models.</summary>
    public OverlayViewModels Views { get; }

    /// <summary>The services, for the overlay's own actions.</summary>
    public IOverlayQueries Queries { get; }

    /// <summary>The renderer in use (<c>uitoolkit</c>, <c>ugui</c>, <c>imgui</c> or <c>none</c>) and why.</summary>
    public string Renderer { get; private set; } = "none";

    /// <inheritdoc cref="Renderer"/>
    public string? RendererReason { get; private set; } = "No renderer has started yet.";

    /// <summary>Records which renderer draws the overlay (set by the renderer selection).</summary>
    public void SetRenderer(string renderer, string? reason)
    {
        Renderer = renderer;
        RendererReason = reason;
    }

    /// <summary>Engages E-STOP (the Control tab's button or its hotkey): the lock closes too.</summary>
    public void EngageEStop()
    {
        Lock.Lock();
        EStop.Engage();
        Toasts.Add("E-STOP engaged: mode ReadOnly, patches reverted, instrumentation removed, jobs and rules cancelled.", ToastLevel.Error, "agent", _lastNow, 8);
    }

    /// <summary>A toast from the client (<c>overlay.notify</c>; main thread). Returns whether it's shown.</summary>
    public bool Notify(string text, ToastLevel level, double seconds) => Toasts.Add(text, level, "client", _lastNow, seconds);

    /// <summary>A question from the client (<c>overlay.prompt</c>; main thread).</summary>
    /// <exception cref="ArgumentException">A prompt with that id is waiting, or the buttons are invalid.</exception>
    public Prompt ShowPrompt(string id, string? title, string message, IReadOnlyList<string> buttons, int? timeoutMs, string? attachProbe, string? textButton) =>
        Prompts.Show(id, title, message, buttons, _lastNow, timeoutMs, attachProbe, textButton);

    /// <summary>Every frame (main thread), with unscaled realtime in seconds.</summary>
    public void Tick(double now)
    {
        _lastNow = now;
        Prompts.Tick(now);
        Toasts.Tick(now);
        Highlights.Tick(now);
        Views.Tick(now);
    }

    private static string FormatAgo(double seconds) => seconds < 60 ? $"{seconds:0}s ago"
        : seconds < 3600 ? $"{seconds / 60:0}m ago" : $"{seconds / 3600:0.#}h ago";

    // The overlay's own state as the views see it (under "overlay").
    private JsonObject LocalState()
    {
        var now = _lastNow;
        var selection = Selection.Current;
        var state = new JsonObject
        {
            { "state", new JsonString(OverlayModel.Name(Model.State)) },
            { "edge", new JsonString(OverlayModel.Name(Model.Edge)) },
            { "offset", new JsonNumber(Model.Offset) },
            { "docked", Model.Docked ? JsonBoolean.True : JsonBoolean.False },
            { "tab", new JsonString(Model.Tab) },
            { "tabs", new JsonArray(Settings.VisibleTabs.Select(t => (JsonValue)new JsonString(t))) },
            { "pickMode", Model.PickMode ? JsonBoolean.True : JsonBoolean.False },
            { "renderer", new JsonString(Renderer) },
            { "estop", new JsonObject { { "engaged", EStop.Engaged ? JsonBoolean.True : JsonBoolean.False }, { "failures", new JsonArray(EStop.Failures.Select(f => (JsonValue)new JsonString(f))) } } },
            { "lock", new JsonObject
                {
                    { "locked", Lock.Locked ? JsonBoolean.True : JsonBoolean.False },
                    { "allowsEdits", Lock.AllowsEdits ? JsonBoolean.True : JsonBoolean.False },
                    { "why", Lock.WhyLocked is { } why ? new JsonString(why) : JsonNull.Instance },
                }
            },
            { "selection", selection is null ? JsonNull.Instance : new JsonObject
                {
                    { "label", new JsonString(selection.Label) },
                    { "locator", selection.Locator is { } l ? new JsonString(l) : JsonNull.Instance },
                    { "destroyed", selection.Destroyed ? JsonBoolean.True : JsonBoolean.False },
                    { "canBack", Selection.CanGoBack ? JsonBoolean.True : JsonBoolean.False },
                    { "canForward", Selection.CanGoForward ? JsonBoolean.True : JsonBoolean.False },
                }
            },
            { "toasts", new JsonArray(Toasts.Visible.Select(t => (JsonValue)new JsonObject
                {
                    { "id", new JsonNumber(t.Id) },
                    { "text", new JsonString(t.Count > 1 ? $"{t.Text} (×{t.Count})" : t.Text) },
                    { "level", new JsonString(t.Level.ToString().ToLowerInvariant()) },
                })) },
            { "prompts", new JsonArray(Prompts.Pending.Select(p => (JsonValue)new JsonObject
                {
                    { "id", new JsonString(p.Id) },
                    { "title", p.Title is { } title ? new JsonString(title) : JsonNull.Instance },
                    { "message", new JsonString(p.Message) },
                    { "buttons", new JsonArray(p.Buttons.Select(b => (JsonValue)new JsonString(b))) },
                    { "textButton", p.TextButton is { } tb ? new JsonString(tb) : JsonNull.Instance },
                    { "remainingMs", p.Remaining(now) is { } r ? new JsonNumber((long)(r * 1000)) : JsonNull.Instance },
                })) },

            // The pending prompts as list rows (views can't nest lists): a question row, then a row per answer.
            { "promptRows", new JsonArray(Prompts.Pending.SelectMany(p => new[]
                {
                    (JsonValue)new JsonObject
                    {
                        { "kind", new JsonString("question") },
                        { "text", new JsonString((p.Title is { } t ? t + ": " : string.Empty) + p.Message) },
                    },
                }.Concat(p.Buttons.Select(b => (JsonValue)new JsonObject
                {
                    { "kind", new JsonString("answer") },
                    { "id", new JsonString(p.Id) },
                    { "button", new JsonString(b) },
                })))) },
            { "notifications", new JsonArray(Toasts.History.Reverse().Select(t => (JsonValue)new JsonObject
                {
                    { "time", new JsonString(FormatAgo(now - t.ShownAt)) },
                    { "level", new JsonString(t.Level.ToString().ToLowerInvariant()) },
                    { "text", new JsonString(t.Count > 1 ? $"{t.Text} (×{t.Count})" : t.Text) },
                })) },
        };
        AddHostState(state);
        return state;
    }
}
