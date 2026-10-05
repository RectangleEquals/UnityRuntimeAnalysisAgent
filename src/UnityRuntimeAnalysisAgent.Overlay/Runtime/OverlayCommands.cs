using System;
using System.Collections.Generic;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Overlay.Views;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>
/// What a click in the overlay runs: the built-in commands (tabs, collapsing, E-STOP, prompts, toasts), and handlers that
/// the tabs register (agent actions go through the controller's queries, recorded as <c>source: overlay</c>). Shared by
/// the renderers, so a button does the same thing however it's drawn.
/// </summary>
public sealed class OverlayCommands
{
    private readonly OverlayController _controller;
    private readonly IAgentLogger _log;
    private readonly Dictionary<string, Action<JsonObject>> _handlers = new(StringComparer.Ordinal);

    /// <summary>Creates the router with the built-in commands.</summary>
    public OverlayCommands(OverlayController controller, IAgentLogger log)
    {
        _controller = controller;
        _log = log;
        Register("tab.open", args => _controller.Model.SelectTab(Text(args, "tab")));
        Register("overlay.toggle", _ => _controller.Model.Toggle());
        Register("overlay.collapse", _ => _controller.Model.SetState(OverlayVisibility.Collapsed));
        Register("overlay.hide", _ => _controller.Model.Hide());
        Register("overlay.dock", args => _controller.Model.SetDocked(!(args["docked"] is JsonBoolean { Value: false })));
        Register("estop", _ =>
        {
            if (!_controller.EStop.Engaged)
            {
                _controller.EngageEStop();
            }
        });
        Register("prompt.answer", args => _controller.Prompts.Answer(Text(args, "id"), Text(args, "button")));
        Register("prompt.sendText", _ => _controller.Prompts.SendText());
        Register("prompt.cancelText", _ => _controller.Prompts.CancelText());
        Register("toast.dismiss", args =>
        {
            if (args["id"] is JsonNumber id)
            {
                _controller.Toasts.Dismiss((long)id.GetDouble());
            }
        });

        // Any agent method, as an audited overlay action: {method, params?, done?}. Errors become a warning toast; the
        // visible tab refreshes afterwards.
        Register("agent.call", args =>
        {
            var method = Text(args, "method");
            var parameters = args["params"] as JsonObject ?? new JsonObject();
            var done = args["done"] is JsonString d ? d.Value : null;
            if (method == "agent.setMode" && !(parameters["mode"] is JsonString mode && _controller.CanLowerTo(mode.Value)))
            {
                _controller.Toasts.Add("The overlay can only lower the agent's mode.", ToastLevel.Warning, "agent", 0, 5);
                return;
            }

            _controller.Queries.Act(method, parameters, (_, error) =>
            {
                if (error is not null)
                {
                    _controller.Toasts.Add($"{method}: {error.Message}", ToastLevel.Warning, "agent", 0, 6);
                }
                else if (done is not null)
                {
                    _controller.Toasts.Add(done, ToastLevel.Success, "agent", 0, 3);
                }

                _controller.Views.Refresh(_controller.Model.Tab);
            });
        });

        Register("report.copy", _ => _controller.BuildReport(markdown =>
        {
            UnityEngine.GUIUtility.systemCopyBuffer = markdown;
            _controller.Toasts.Add($"Report copied ({markdown.Length / 1024.0:0.#} KiB).", ToastLevel.Success, "agent", 0, 3);
        }));

        Register("setting.toggle", args => _controller.ToggleSetting(Text(args, "key")));
        Register("setting.cycle", args => _controller.CycleSetting(Text(args, "key")));
        Register("setting.step", args => _controller.StepSetting(Text(args, "key"), args["delta"] is JsonNumber n ? (int)n.GetDouble() : 1));
        Register("setting.edit", args => _controller.EditSetting(Text(args, "key")));
        Register("setting.reset", args => _controller.ResetSetting(Text(args, "key")));

        Register("inspector.scene", args => _controller.SelectScene(Text(args, "scene")));
        Register("inspector.select", args =>
        {
            if (args["h"] is JsonNumber h)
            {
                _controller.SelectHandle((long)h.GetDouble(), Text(args, "label"), args["locator"] is JsonString l ? l.Value : null);
            }
        });
        Register("inspector.back", _ => Refresh(_controller.Selection.Back()));
        Register("inspector.forward", _ => Refresh(_controller.Selection.Forward()));
        Register("inspector.lock", _ =>
        {
            _controller.ToggleLock();
            _controller.Views.Refresh("inspector");
        });
        Register("inspector.send", _ => _controller.SendSelection());

        Register("logs.level", args => _controller.SetLogLevel(Text(args, "level")));
        Register("activity.filter", args => _controller.SetActivityFilter(Text(args, "filter")));
        Register("client.disconnect", _ => _controller.DisconnectClients());

        // Local control (the design's "local control": works in any mode, never through a client).
        Register("audio.toggle", _ =>
        {
            UnityEngine.AudioListener.pause = !UnityEngine.AudioListener.pause;
            _controller.Toasts.Add(UnityEngine.AudioListener.pause ? "Audio paused." : "Audio resumed.", ToastLevel.Info, "agent", 0, 2);
        });
    }

    private void Refresh(bool changed)
    {
        if (changed)
        {
            _controller.Views.Refresh("inspector");
        }
    }

    /// <summary>Adds (or replaces) a command.</summary>
    public void Register(string command, Action<JsonObject> handler) => _handlers[command] = handler;

    /// <summary>Runs a command. Unknown commands and handler failures are logged, never thrown into the game.</summary>
    public void Run(string command, JsonObject? args, ViewPresenter? presenter = null)
    {
        args ??= new JsonObject();
        try
        {
            if (command == "tab.select" && presenter is not null)
            {
                presenter.SelectTab(Text(args, "path"), args["index"] is JsonNumber n ? (int)n.GetDouble() : 0);
                return;
            }

            if (_handlers.TryGetValue(command, out var handler))
            {
                handler(args);
                return;
            }

            _log.Warning($"Overlay: no command '{command}'.");
        }
        catch (Exception e)
        {
            _log.Warning($"Overlay: command '{command}' failed: {e.Message}");
        }
    }

    private static string Text(JsonObject args, string name) => args[name] is JsonString s ? s.Value : args[name]?.ToString() ?? "";
}
