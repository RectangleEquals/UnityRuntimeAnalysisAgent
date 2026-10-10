using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Input;
using UnityRuntimeAnalysisAgent.Core.Jobs;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// The input methods: what can drive this game's input, and input sessions — begin (the user is
/// warned with a countdown), held state, taps, timed sequences, resume after a takeover, end.
/// </summary>
internal sealed class InputService
{
    private static readonly HashSet<string> MouseButtons = new(StringComparer.Ordinal) { "left", "right", "middle", "back", "forward" };

    private readonly InputSessions _sessions;
    private readonly MainThreadPump _pump;
    private readonly JobManager _jobs;

    public InputService(InputSessions sessions, MainThreadPump pump, JobManager jobs)
    {
        _sessions = sessions;
        _pump = pump;
        _jobs = jobs;
    }

    private long Frame => _pump.Clock.FrameCount;

    private long RealtimeMs => (long)(_pump.Clock.Realtime * 1000);

    [RpcMethod(Methods.InputCapabilities)]
    public ProtocolMessage Capabilities(RequestContext context, InputCapabilitiesParams p)
    {
        var layers = _sessions.Layers;
        var available = layers.Where(l => l.Status.Available).ToList();
        return new InputCapabilitiesResult
        {
            Enabled = _sessions.Settings.Enabled,
            Layers = layers.Select(l =>
            {
                var status = l.Status;
                return new InputLayerInfo { Layer = l.Id, Available = status.Available, Version = status.Version, Drives = status.Drives.ToList(), Reason = status.Reason };
            }).ToList(),
            Recommended = _sessions.Recommended?.Id,
            Devices = available.SelectMany(l => l.Devices().Select(d => new InputDeviceInfo { Kind = d.Kind, Name = d.Name, Layer = l.Id })).ToList(),
            Actions = available.SelectMany(l => l.Actions().Select(a => new InputActionInfo { Layer = l.Id, Name = a.Name, Kind = a.Kind, Player = a.Player })).ToList(),
            Axes = available.SelectMany(l => l.Axes()).Distinct(StringComparer.Ordinal).ToList(),
            Session = _sessions.Current is { } s ? _sessions.Info(s) : null,
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    /// <summary>Opens a session and waits through the countdown (or until the user takes over).</summary>
    [RpcMethod(Methods.InputBegin, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 120_000)]
    public IEnumerable<object?> Begin(RequestContext context, InputBeginParams p)
    {
        var session = _sessions.Begin(context.Connection?.Id ?? 0, context.Client ?? "client", p.Reason, p.Layers, p.Devices, p.CountdownMs, p.Takeover, p.MaxDurationMs);
        while (session.State == InputSessionState.Countdown)
        {
            yield return PumpWait.NextFrame;
        }

        yield return _sessions.Info(session);
    }

    [RpcMethod(Methods.InputSet)]
    public ProtocolMessage Set(RequestContext context, InputSetParams p)
    {
        var session = _sessions.RequireActive(p.SessionId);
        var layers = Layers(session, p.Layer);
        Apply(p.State);
        return new InputSetResult { Layers = layers.Select(l => l.Id).ToList(), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    /// <summary>Presses one input and completes after its release.</summary>
    [RpcMethod(Methods.InputTap, DefaultTimeoutMs = 30_000, MaxTimeoutMs = 120_000)]
    public IEnumerable<object?> Tap(RequestContext context, InputTapParams p)
    {
        var session = _sessions.RequireActive(p.SessionId);
        var layer = Layers(session, p.Layer)[0];
        var tap = Press(p.Press);
        while (!tap.Done)
        {
            yield return PumpWait.NextFrame;
        }

        yield return new InputTapResult { Layer = layer.Id, DownFrame = tap.DownFrame, UpFrame = tap.UpFrame, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    /// <summary>Runs a timed script of steps, frame by frame, as a job.</summary>
    [RpcMethod(Methods.InputSequenceStart)]
    public ProtocolMessage SequenceStart(RequestContext context, InputSequenceStartParams p)
    {
        var session = _sessions.RequireActive(p.SessionId);
        Layers(session, p.Layer);
        var steps = p.Steps.ToList(); // in the given order, each when it's due
        if (steps.Count == 0)
        {
            throw ProtocolException.InvalidParams("params.steps", "A sequence needs at least one step.");
        }

        foreach (var step in steps)
        {
            Validate(step);
        }

        return _jobs.Start("input.sequence", job =>
        {
            var run = new SequenceRun(session, steps, this);
            _pump.Ticked += run.Tick;
            try
            {
                while (!run.Finished && !job.Cancellation.IsCancellationRequested)
                {
                    job.Progress("steps", run.StepsRun, steps.Count, null);
                    job.Cancellation.WaitHandle.WaitOne(50);
                }
            }
            finally
            {
                _pump.Ticked -= run.Tick;
            }

            if (job.Cancellation.IsCancellationRequested && !run.Finished)
            {
                run.Stop("cancel");
                _sessions.Virtual.ReleaseAll();
            }

            return new InputSequenceStartJobResult
            {
                StepsRun = run.StepsRun,
                Completed = run.StoppedBy is null,
                StoppedBy = run.StoppedBy,
                StartFrame = run.StartFrame,
                EndFrame = run.EndFrame,
            };
        });
    }

    /// <summary>Resumes a paused session (after the user agreed); waits through the countdown.</summary>
    [RpcMethod(Methods.InputResume, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 120_000)]
    public IEnumerable<object?> Resume(RequestContext context, InputResumeParams p)
    {
        var session = _sessions.Resume(p.SessionId, p.CountdownMs);
        while (session.State == InputSessionState.Countdown)
        {
            yield return PumpWait.NextFrame;
        }

        yield return _sessions.Info(session);
    }

    /// <summary>Ends a session after its notice; completes when control is released.</summary>
    [RpcMethod(Methods.InputEnd, DefaultTimeoutMs = 60_000, MaxTimeoutMs = 120_000)]
    public IEnumerable<object?> End(RequestContext context, InputEndParams p)
    {
        var session = _sessions.Find(p.SessionId);
        _sessions.End(p.SessionId, p.CountdownMs);
        while (session.State != InputSessionState.Ended)
        {
            yield return PumpWait.NextFrame;
        }

        yield return new InputEndResult { DurationMs = (long)Math.Max(0, (_pump.Clock.Realtime - session.StartedAt) * 1000), Frame = Frame, RealtimeMs = RealtimeMs };
    }

    // The layers a call goes to: the named one (it must be one of the session's), else the session's.
    private static IReadOnlyList<IInputLayer> Layers(InputSession session, string? name)
    {
        if (name is null)
        {
            return session.Layers;
        }

        var layer = session.Layers.FirstOrDefault(l => l.Id == name)
            ?? throw new ProtocolException(ErrorCodes.InvalidParams, $"The session doesn't use the {name} layer (it uses {string.Join(", ", session.Layers.Select(l => l.Id))}).", new JsonObject { { "param", new JsonString("params.layer") } });
        return new[] { layer };
    }

    // Sets the groups a state gives (each replaces its own group).
    private void Apply(InputState state)
    {
        var input = _sessions.Virtual;
        if (state.Keys is { } keys)
        {
            input.SetKeys(keys);
        }

        if (state.Mouse is { } mouse)
        {
            if (mouse.Buttons?.FirstOrDefault(b => !MouseButtons.Contains(b)) is { } bad)
            {
                throw ProtocolException.InvalidParams("params.state.mouse.buttons", $"'{bad}' isn't a mouse button.");
            }

            input.SetMouse(mouse.Buttons, V(mouse.Position), V(mouse.Delta), V(mouse.Wheel));
        }

        if (state.Gamepad is { } pad)
        {
            input.SetGamepad(pad.Buttons, V(pad.LeftStick), V(pad.RightStick), pad.LeftTrigger, pad.RightTrigger);
        }

        if (state.Actions is { } actions)
        {
            input.SetActions(actions.Select(a => ((int)(a.Player ?? 0), a.Name, Value(a.Value))).ToList());
        }
    }

    private Tap Press(InputPress press)
    {
        var (kind, name) = What(press);
        var frames = (int)Math.Max(1, Math.Min(press.HoldFrames ?? 1, 100_000));
        var seconds = Math.Max(0, Math.Min(press.HoldMs ?? 0, 600_000)) / 1000.0;
        return _sessions.Virtual.Press(kind, name, (int)(press.Player ?? 0), frames, seconds);
    }

    private static (InputKind Kind, string Name) What(InputPress press)
    {
        var given = new[] { press.Key, press.MouseButton, press.GamepadButton, press.Action }.Count(x => x is not null);
        if (given != 1)
        {
            throw ProtocolException.InvalidParams("params.press", "A press names exactly one of key, mouseButton, gamepadButton or action.");
        }

        return press.Key is { } key ? (InputKind.Key, key)
            : press.MouseButton is { } button ? (InputKind.MouseButton, button)
            : press.GamepadButton is { } pad ? (InputKind.GamepadButton, pad)
            : (InputKind.Action, press.Action!);
    }

    private static void Validate(InputSequenceStep step)
    {
        if (step.AtMs is not null && step.AtFrame is not null)
        {
            throw ProtocolException.InvalidParams("params.steps", "A step has atMs or atFrame, not both.");
        }

        if (step.Tap is { } tap)
        {
            What(tap);
        }
    }

    private static (double X, double Y)? V(InputVector2? v) => v is null ? null : (v.X, v.Y);

    private static ActionValue Value(JsonValue value) => value switch
    {
        JsonBoolean b => ActionValue.Button(b.Value),
        JsonNumber n => ActionValue.Of(n.GetDouble()),
        JsonObject o when o["x"] is JsonNumber x && o["y"] is JsonNumber y => ActionValue.Of(x.GetDouble(), y.GetDouble()),
        _ => throw ProtocolException.InvalidParams("params.state.actions", "An action value is a boolean, a number or {x, y}."),
    };

    // A sequence in progress: steps due by frame or time since its first frame, run on the main thread.
    private sealed class SequenceRun
    {
        private readonly InputSession _session;
        private readonly List<InputSequenceStep> _steps;
        private readonly InputService _service;
        private double _startTime;

        public SequenceRun(InputSession session, List<InputSequenceStep> steps, InputService service)
        {
            _session = session;
            _steps = steps;
            _service = service;
        }

        public long StartFrame { get; private set; } = -1;

        public long EndFrame { get; private set; }

        public int StepsRun { get; private set; }

        public string? StoppedBy { get; private set; }

        public bool Finished { get; private set; }

        public void Stop(string reason)
        {
            StoppedBy ??= reason;
            Finished = true;
        }

        public void Tick(FrameTime clock)
        {
            if (Finished)
            {
                return;
            }

            if (_session.State != InputSessionState.Active)
            {
                EndFrame = clock.FrameCount;
                Stop(_session.State == InputSessionState.Paused ? "takeover" : "end");
                return;
            }

            if (StartFrame < 0)
            {
                StartFrame = clock.FrameCount;
                _startTime = clock.Realtime;
            }

            var frames = clock.FrameCount - StartFrame;
            var ms = (clock.Realtime - _startTime) * 1000;
            while (StepsRun < _steps.Count && Due(_steps[StepsRun], frames, ms))
            {
                var step = _steps[StepsRun++];
                if (step.Release == true)
                {
                    _service._sessions.Virtual.ReleaseAll();
                }

                if (step.Set is { } set)
                {
                    _service.Apply(set);
                }

                if (step.Tap is { } tap)
                {
                    _service.Press(tap);
                }
            }

            EndFrame = clock.FrameCount;
            if (StepsRun == _steps.Count)
            {
                Finished = true;
            }
        }

        private static bool Due(InputSequenceStep step, long frames, double ms) =>
            step.AtFrame is { } f ? frames >= f : ms >= (step.AtMs ?? 0);
    }
}
