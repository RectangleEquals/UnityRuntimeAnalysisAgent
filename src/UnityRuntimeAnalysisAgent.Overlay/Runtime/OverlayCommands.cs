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
