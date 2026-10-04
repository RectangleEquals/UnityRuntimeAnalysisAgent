using System;
using System.Collections.Generic;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Overlay;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// The overlay's protocol methods: <c>overlay.notify</c> shows a toast from the client in the game; <c>overlay.prompt</c>
/// asks the user a question with buttons (one may open a text field), answered through <c>overlay.promptResult</c>.
/// </summary>
internal sealed class OverlayService
{
    private readonly OverlayController _overlay;

    public OverlayService(OverlayController overlay) => _overlay = overlay;

    [RpcMethod(Methods.OverlayNotify)]
    public ProtocolMessage Notify(RequestContext context, OverlayNotifyParams p)
    {
        var level = p.Level switch
        {
            "success" => ToastLevel.Success,
            "warning" => ToastLevel.Warning,
            "error" => ToastLevel.Error,
            _ => ToastLevel.Info,
        };
        var seconds = p.DurationMs is > 0 ? p.DurationMs.Value / 1000.0 : 4.0;
        return new OverlayNotifyResult { Shown = _overlay.Notify(p.Text, level, seconds) };
    }

    [RpcMethod(Methods.OverlayPrompt)]
    public ProtocolMessage Prompt(RequestContext context, OverlayPromptParams p)
    {
        var buttons = p.Buttons is { Count: > 0 } list ? list : new List<string> { "OK" };
        try
        {
            _overlay.ShowPrompt(p.Id, p.Title, p.Message, buttons, p.TimeoutMs is { } ms ? (int)Math.Min(ms, int.MaxValue) : null, p.AttachProbe, p.TextButton);
        }
        catch (ArgumentException e)
        {
            throw new ProtocolException(ErrorCodes.InvalidParams, e.Message);
        }

        return new OverlayPromptResult { Shown = true };
    }
}
