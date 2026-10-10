using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;

namespace UnityRuntimeAnalysisAgent.Core.Input;

/// <summary>The state of an input session (protocol names in <see cref="InputSessions.Name"/>).</summary>
public enum InputSessionState
{
    /// <summary>The user is being warned; nothing is injected yet.</summary>
    Countdown,

    /// <summary>Input is injected.</summary>
    Active,

    /// <summary>The user took over; everything virtual is released until a resume.</summary>
    Paused,

    /// <summary>Finished.</summary>
    Ended,
}

/// <summary>An input session: who drives the game, why, through which layers, and until when.</summary>
public sealed class InputSession
{
    internal InputSession(string id, long connection, string client, string reason, IReadOnlyList<IInputLayer> layers, IReadOnlyList<string> devices, bool userTakeover, double startedAt, double? endsAt)
    {
        Id = id;
        Connection = connection;
        Client = client;
        Reason = reason;
        Layers = layers;
        Devices = devices;
        UserTakeover = userTakeover;
        StartedAt = startedAt;
        EndsAt = endsAt;
    }

    /// <summary>The session id.</summary>
    public string Id { get; }

    /// <summary>The connection that began it.</summary>
    public long Connection { get; }

    /// <summary>The client's name.</summary>
    public string Client { get; }

    /// <summary>Why, as shown to the user.</summary>
    public string Reason { get; }

    /// <summary>The layers it drives, in order of preference.</summary>
    public IReadOnlyList<IInputLayer> Layers { get; }

    /// <summary>What it drives (for the warning).</summary>
    public IReadOnlyList<string> Devices { get; }

    /// <summary>Whether any real input takes over (else only the chord).</summary>
    public bool UserTakeover { get; }

    /// <summary>The state.</summary>
    public InputSessionState State { get; internal set; }

    /// <summary>When it began (realtime seconds).</summary>
    public double StartedAt { get; }

    /// <summary>When the time limit ends it (realtime seconds), if any.</summary>
    public double? EndsAt { get; internal set; }

    /// <summary>When the countdown finishes (countdown state).</summary>
    public double CountdownUntil { get; internal set; }

    /// <summary>When an announced end releases control, if one is announced.</summary>
    public double? EndingAt { get; internal set; }

    /// <summary>When the session last became active (takeover grace).</summary>
    internal double ActiveSince { get; set; }
}

/// <summary>What the overlay shows about the input session.</summary>
public sealed class InputNotice
{
    internal InputNotice(InputSessionState state, string client, string reason, IReadOnlyList<string> devices, double? secondsLeft, bool ending, string takeover)
    {
        State = state;
        Client = client;
        Reason = reason;
        Devices = devices;
        SecondsLeft = secondsLeft;
        Ending = ending;
        Takeover = takeover;
    }

    /// <summary>The session's state.</summary>
    public InputSessionState State { get; }

    /// <summary>Which client drives the game.</summary>
    public string Client { get; }

    /// <summary>Why.</summary>
    public string Reason { get; }

    /// <summary>What is driven (keyboard, mouse, gamepad, actions).</summary>
    public IReadOnlyList<string> Devices { get; }

    /// <summary>Seconds left in a countdown or an announced end.</summary>
    public double? SecondsLeft { get; }

    /// <summary>Whether control is about to end.</summary>
    public bool Ending { get; }

    /// <summary>How to take over, as text.</summary>
    public string Takeover { get; }
}

/// <summary>
/// Input sessions: the only way virtual input reaches the game. One at a time; a countdown
/// warning before input starts; any real input in the game (or the takeover chord) pauses it and releases everything;
/// a time limit, E-STOP, the client disconnecting or the user switching input driving off ends it. <see cref="Tick"/>
/// runs every frame on the main thread.
/// </summary>
public sealed class InputSessions
{
    private const double TakeoverGraceSeconds = 0.25; // real input right after a session starts is the user letting go

    private readonly object _gate = new();
    private readonly List<IInputLayer> _layers;
    private readonly Func<(long Frame, double Realtime)> _clock;
    private int _next;
    private InputSession? _current;
    private bool? _backgroundBefore;

    /// <summary>Creates the sessions over the game's input layers.</summary>
    public InputSessions(InputSettings settings, IEnumerable<IInputLayer> layers, Func<(long Frame, double Realtime)> clock)
    {
        Settings = settings;
        _layers = layers.ToList();
        _clock = clock;
    }

    /// <summary>The settings.</summary>
    public InputSettings Settings { get; }

    /// <summary>The virtual input the layers merge.</summary>
    public VirtualInput Virtual { get; } = new();

    /// <summary>The input layers of this game.</summary>
    public IReadOnlyList<IInputLayer> Layers => _layers;

    /// <summary>Sends <c>input.session</c> to clients.</summary>
    public Action<InputSessionEventParams> Emit { get; set; } = _ => { };

    /// <summary>Whether the game has focus (takeover by real input only counts then).</summary>
    public Func<bool> GameFocused { get; set; } = () => true;

    /// <summary>Whether the agent's mode still allows driving the game (a session ends when it doesn't).</summary>
    public Func<bool> Allowed { get; set; } = () => true;

    /// <summary>Reads and sets <c>Application.runInBackground</c> (held on during a session), when available.</summary>
    public Func<bool>? GetRunInBackground { get; set; }

    /// <summary>Sets <c>Application.runInBackground</c>.</summary>
    public Action<bool>? SetRunInBackground { get; set; }

    /// <summary>Raised when the session or its notice changes (the overlay redraws).</summary>
    public event Action? Changed;

    /// <summary>The open session (not ended), if any.</summary>
    public InputSession? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The protocol name of a state.</summary>
    public static string Name(InputSessionState state) => state switch
    {
        InputSessionState.Countdown => "countdown",
        InputSessionState.Active => "active",
        InputSessionState.Paused => "paused",
        _ => "ended",
    };

    /// <summary>What the overlay shows now, or null without a session.</summary>
    public InputNotice? Notice
    {
        get
        {
            lock (_gate)
            {
                if (_current is not { } s)
                {
                    return null;
                }

                var now = _clock().Realtime;
                double? left = s.State == InputSessionState.Countdown ? Math.Max(0, s.CountdownUntil - now)
                    : s.EndingAt is { } at ? Math.Max(0, at - now) : null;
                var takeover = (s.UserTakeover ? "use your controls or press " : "press ") + InputSettings.Format(Settings.TakeoverKey)
                    + " (pad: " + InputSettings.Format(Settings.TakeoverPad) + ")";
                return new InputNotice(s.State, s.Client, s.Reason, s.Devices, left, s.EndingAt is not null, takeover);
            }
        }
    }

    /// <summary>The layers a call may use: the named ones, else the available ones in order of preference.</summary>
    public IReadOnlyList<IInputLayer> Pick(IReadOnlyList<string>? names)
    {
        var available = _layers.Where(l => l.Status.Available).ToList();
        if (names is { Count: > 0 })
        {
            var picked = new List<IInputLayer>();
            foreach (var name in names)
            {
                var layer = _layers.FirstOrDefault(l => l.Id == name)
                    ?? throw new ProtocolException(ErrorCodes.Unsupported, $"There is no input layer '{name}' in this agent.");
                if (!layer.Status.Available)
                {
                    throw new ProtocolException(ErrorCodes.Unsupported, $"The {name} layer doesn't apply to this game: {layer.Status.Reason}");
                }

                picked.Add(layer);
            }

            return picked;
        }

        if (available.Count == 0)
        {
            throw new ProtocolException(ErrorCodes.Unsupported, "No input layer applies to this game (see input.capabilities).");
        }

        return available;
    }

    /// <summary>The recommended layer: one with the game's actions first, then the others in their order.</summary>
    public IInputLayer? Recommended => _layers.Where(l => l.Status.Available).OrderBy(l => l.Status.Drives.Contains("actions") ? 0 : 1).FirstOrDefault();

    /// <summary>Opens a session in its countdown. <c>BUSY</c> while another is open.</summary>
    public InputSession Begin(long connection, string client, string reason, IReadOnlyList<string>? layers, IReadOnlyList<string>? devices, long? countdownMs, string? takeover, long? maxDurationMs)
    {
        if (!Settings.Enabled)
        {
            throw new ProtocolException(ErrorCodes.Unsupported, "The user has switched input driving off (Input.Enabled).");
        }

        var picked = Pick(layers);
        InputSession session;
        lock (_gate)
        {
            if (_current is { } open)
            {
                throw new ProtocolException(ErrorCodes.Busy, $"An input session is already open ({open.Id}).", new JsonObject { { "sessionId", new JsonString(open.Id) } });
            }

            var now = _clock().Realtime;
            var limit = Math.Min(maxDurationMs ?? Settings.MaxSessionMs, Settings.MaxSessionMs) / 1000.0;
            var drives = devices is { Count: > 0 } ? devices : picked.SelectMany(l => l.Status.Drives).Distinct().ToList();
            session = new InputSession("in-" + (++_next).ToString(CultureInfo.InvariantCulture), connection, client, reason, picked, drives, takeover != "chord", now, now + limit)
            {
                State = InputSessionState.Countdown,
                CountdownUntil = now + Math.Max(0, countdownMs ?? Settings.CountdownMs) / 1000.0,
            };
            _current = session;
            if (GetRunInBackground is { } get && SetRunInBackground is { } set)
            {
                _backgroundBefore = get();
                set(true);
            }
        }

        Publish(session, "client");
        return session;
    }

    /// <summary>The open session by id; <c>NOT_FOUND</c> otherwise.</summary>
    public InputSession Find(string id)
    {
        lock (_gate)
        {
            return _current is { } s && s.Id == id ? s : throw new ProtocolException(ErrorCodes.NotFound, $"No input session {id}.");
        }
    }

    /// <summary>The session, when it's active; <c>SESSION_INACTIVE</c> (with its state) otherwise.</summary>
    public InputSession RequireActive(string id)
    {
        var session = Find(id);
        if (session.State != InputSessionState.Active)
        {
            var name = Name(session.State);
            var hint = session.State == InputSessionState.Paused ? " (the user took over); ask them, then input.resume" : " (still counting down)";
            throw new ProtocolException(ErrorCodes.SessionInactive, $"Input session {id} is {name}{hint}.", new JsonObject { { "state", new JsonString(name) } });
        }

        return session;
    }

    /// <summary>Restarts a paused session's countdown.</summary>
    public InputSession Resume(string id, long? countdownMs)
    {
        var session = Find(id);
        lock (_gate)
        {
            if (session.State == InputSessionState.Paused)
            {
                session.State = InputSessionState.Countdown;
                session.CountdownUntil = _clock().Realtime + Math.Max(0, countdownMs ?? Settings.CountdownMs) / 1000.0;
            }
        }

        Publish(session, "client");
        return session;
    }

    /// <summary>Announces the end (control is released when the notice runs out); 0 ends at once.</summary>
    public void End(string id, long? countdownMs)
    {
        var session = Find(id);
        var notice = Math.Max(0, countdownMs ?? 3000) / 1000.0;
        if (notice <= 0 || session.State != InputSessionState.Active)
        {
            Finish(session, "client");
            return;
        }

        lock (_gate)
        {
            session.EndingAt = _clock().Realtime + notice;
        }

        Changed?.Invoke();
    }

    /// <summary>Ends the open session at once (E-STOP, the setting switched off).</summary>
    public void EndNow(string reason)
    {
        if (Current is { } session)
        {
            Finish(session, reason);
        }
    }

    /// <summary>Ends the session a connection began, when it disconnects.</summary>
    public void OnDisconnect(long connection)
    {
        if (Current is { } session && session.Connection == connection)
        {
            Finish(session, "disconnect");
        }
    }

    /// <summary>
    /// One frame (main thread): advances the virtual input, starts a session whose countdown ran out, ends one whose
    /// time or announced end ran out, and pauses one the user takes over.
    /// </summary>
    public void Tick()
    {
        var (frame, now) = _clock();
        Virtual.Advance(frame, now);
        if (Current is not { } session)
        {
            return;
        }

        if (!Settings.Enabled)
        {
            Finish(session, "disabled");
            return;
        }

        if (!Allowed())
        {
            Finish(session, "client");
            return;
        }

        if (Chord())
        {
            Pause(session, "chord");
            return;
        }

        switch (session.State)
        {
            case InputSessionState.Countdown when session.UserTakeover && GameFocused() && _layers.Any(l => l.Status.Available && l.RealInput()):
                Pause(session, "user");
                return;
            case InputSessionState.Countdown when now >= session.CountdownUntil:
                Activate(session, now);
                return;
            case InputSessionState.Active when session.EndingAt is { } at && now >= at:
                Finish(session, "client");
                return;
            case InputSessionState.Active when session.EndsAt is { } limit && now >= limit:
                Finish(session, "maxDuration");
                return;
            case InputSessionState.Active when session.UserTakeover && now - session.ActiveSince > TakeoverGraceSeconds && GameFocused()
                && session.Layers.Any(l => l.RealInput()):
                Pause(session, "user");
                return;
        }

        if (session.State == InputSessionState.Countdown || session.EndingAt is not null)
        {
            Changed?.Invoke(); // the countdown's seconds
        }
    }

    // The takeover chord, on the keyboard or a pad (works whatever the session's takeover setting).
    private bool Chord() => _layers.Any(l => l.Status.Available && l.RealChord(Settings.TakeoverKey, Settings.TakeoverPad));

    private void Activate(InputSession session, double now)
    {
        lock (_gate)
        {
            session.State = InputSessionState.Active;
            session.ActiveSince = now;
        }

        foreach (var layer in session.Layers)
        {
            layer.Attach(Virtual);
        }

        Publish(session, "client");
    }

    private void Pause(InputSession session, string reason)
    {
        if (session.State is InputSessionState.Paused or InputSessionState.Ended)
        {
            return;
        }

        Release(session);
        lock (_gate)
        {
            session.State = InputSessionState.Paused;
            session.EndingAt = null;
        }

        Publish(session, reason);
    }

    private void Finish(InputSession session, string reason)
    {
        Release(session);
        lock (_gate)
        {
            session.State = InputSessionState.Ended;
            if (ReferenceEquals(_current, session))
            {
                _current = null;
            }

            if (_backgroundBefore is { } before && SetRunInBackground is { } set)
            {
                set(before);
            }

            _backgroundBefore = null;
        }

        Publish(session, reason);
    }

    private void Release(InputSession session)
    {
        Virtual.ReleaseAll();
        foreach (var layer in session.Layers)
        {
            layer.Detach();
        }
    }

    private void Publish(InputSession session, string reason)
    {
        var (frame, now) = _clock();
        Emit(new InputSessionEventParams { SessionId = session.Id, State = Name(session.State), Reason = reason, Frame = frame, RealtimeMs = (long)(now * 1000) });
        Changed?.Invoke();
    }

    /// <summary>The session as the protocol reports it.</summary>
    public InputSessionInfo Info(InputSession session)
    {
        var now = _clock().Realtime;
        return new InputSessionInfo
        {
            SessionId = session.Id,
            State = Name(session.State),
            Client = session.Client,
            Reason = session.Reason,
            Layers = session.Layers.Select(l => l.Id).ToList(),
            Takeover = session.UserTakeover ? "user" : "chord",
            RemainingMs = session.EndsAt is { } end && session.State != InputSessionState.Ended ? (long)Math.Max(0, (end - now) * 1000) : null,
        };
    }
}
