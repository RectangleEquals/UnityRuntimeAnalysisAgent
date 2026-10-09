using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// The overlay's protocol methods: <c>overlay.notify</c> shows a toast from the client in the game; <c>overlay.prompt</c>
/// asks the user a question with buttons (one may open a text field), answered through <c>overlay.promptResult</c>. A
/// client can also drive the overlay itself, as the user would: read and set its state and tab, list its elements, bring
/// one into view, operate controls, type into text boxes, and read or change its settings.
/// </summary>
internal sealed class OverlayService
{
    private const int DefaultSnapshotLimit = 500;
    private const int SettleFrames = 30; // how long a reveal waits for the panel to redraw (a tab switch, a scroll)

    private readonly OverlayController _overlay;
    private readonly DataModel _data;
    private readonly MainThreadPump _pump;

    public OverlayService(OverlayController overlay, DataModel data, MainThreadPump pump)
    {
        _overlay = overlay;
        _data = data;
        _pump = pump;
    }

    private long Frame => _pump.Clock.FrameCount;

    private long RealtimeMs => (long)(_pump.Clock.Realtime * 1000);

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

    [RpcMethod(Methods.OverlayState)]
    public ProtocolMessage State(RequestContext context, OverlayStateParams p)
    {
        var model = _overlay.Model;
        var focused = _overlay.Keyboard.Focused is { } box ? _overlay.Automation?.Elements().FirstOrDefault(e => e.Box == box)?.Id : null;
        return new OverlayStateResult
        {
            Available = _overlay.Automation is not null,
            State = OverlayModel.Name(model.State),
            Edge = OverlayModel.Name(model.Edge),
            Offset = model.Offset,
            Tab = model.Tab,
            PickMode = model.PickMode,
            Renderer = _overlay.Renderer,
            RendererReason = _overlay.RendererReason,
            Tabs = _overlay.Settings.VisibleTabs.ToList(),
            FocusedElement = focused,
        };
    }

    [RpcMethod(Methods.OverlaySetState)]
    public ProtocolMessage SetState(RequestContext context, OverlaySetStateParams p)
    {
        var model = _overlay.Model;
        try
        {
            var state = p.State switch
            {
                null => model.State,
                "hidden" => OverlayVisibility.Hidden,
                "collapsed" => OverlayVisibility.Collapsed,
                "expanded" => OverlayVisibility.Expanded,
                _ => throw new ArgumentException($"Unknown state '{p.State}'."),
            };
            model.SetState(state, p.Tab);
        }
        catch (ArgumentException e)
        {
            throw new ProtocolException(ErrorCodes.InvalidParams, e.Message);
        }

        return new OverlaySetStateResult { State = OverlayModel.Name(model.State), Tab = model.Tab };
    }

    /// <summary>A page of the overlay's elements: read once (on the first page) and paged from that read.</summary>
    [RpcMethod(Methods.OverlaySnapshot)]
    public ProtocolMessage Snapshot(RequestContext context, OverlaySnapshotParams p)
    {
        OverlayPage page;
        if (p.Cursor is { } cursor)
        {
            page = _data.Cursors.Take<OverlayPage>(cursor, "params.cursor");
        }
        else
        {
            IEnumerable<OverlayElementState> all = Automation().Elements();
            if (p.OnlyVisible == true)
            {
                all = all.Where(e => e.Visibility is "visible" or "partial");
            }

            if (p.Interaction is { Count: > 0 } wanted)
            {
                all = all.Where(e => wanted.Contains(e.Interaction));
            }

            if (p.Under is { } under)
            {
                all = all.Where(e => e.Id == under || e.Id.StartsWith(under + "/", StringComparison.Ordinal));
            }

            page = new OverlayPage(all.ToList(), 0);
        }

        var limit = (int)Math.Max(1, Math.Min(p.Limit ?? DefaultSnapshotLimit, 10_000));
        var items = page.Elements.Skip(page.Offset).Take(limit).ToList();
        var next = page.Offset + items.Count;
        return new OverlaySnapshotResult
        {
            State = OverlayModel.Name(_overlay.Model.State),
            Tab = _overlay.Model.Tab,
            Items = items.Select(Element).ToList(),
            Cursor = next < page.Elements.Count ? _data.Cursors.Mint(page with { Offset = next }) : null,
            Total = page.Elements.Count,
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    /// <summary>
    /// Brings an element into view: the panel opens on its tab (an id of another tab's content names its tab:
    /// <c>panel/&lt;tab&gt;/…</c>), its list or scroll view scrolls to it, and it's outlined. Waits for the panel to redraw.
    /// </summary>
    [RpcMethod(Methods.OverlayReveal)]
    public IEnumerable<object?> Reveal(RequestContext context, OverlayRevealParams p)
    {
        var automation = Automation();
        var model = _overlay.Model;
        var element = Find(automation, p.Element);
        var parts = p.Element.Split('/');
        if (parts.Length > 1 && parts[0] == "panel")
        {
            // Panel content: open the panel on its tab.
            var tab = parts[1];
            if (model.State != OverlayVisibility.Expanded || model.Tab != tab)
            {
                try
                {
                    model.SetState(OverlayVisibility.Expanded, tab);
                }
                catch (ArgumentException e)
                {
                    throw new ProtocolException(ErrorCodes.NotFound, $"No overlay element '{p.Element}': {e.Message}");
                }

                element = null;
            }
        }
        else if (element is null && parts[0] == "header" && model.State != OverlayVisibility.Expanded)
        {
            model.SetState(OverlayVisibility.Expanded);
        }

        for (var i = 0; element is null && i < SettleFrames; i++)
        {
            yield return PumpWait.NextFrame;
            element = Find(automation, p.Element);
        }

        if (element is null)
        {
            throw new ProtocolException(ErrorCodes.NotFound, $"No overlay element '{p.Element}'.", Hint());
        }

        // Scroll, let the panel redraw, and again while it isn't in view (a list inside a scrolling tab scrolls twice: the
        // list brings its row in, then the tab brings the list's row in).
        for (var i = 0; i < SettleFrames && element is not null && element.Visibility is not "visible"; i++)
        {
            automation.ScrollIntoView(p.Element);
            yield return PumpWait.NextFrame;
            element = Find(automation, p.Element);
        }

        if (p.Highlight != false && element is not null)
        {
            automation.Outline(p.Element, (p.DurationMs ?? 2000) / 1000.0);
        }

        yield return new OverlayRevealResult
        {
            Revealed = element?.Visibility is "visible" or "partial",
            Tab = model.Tab,
            Visibility = element?.Visibility ?? "hidden",
            Rect = Rect(element?.Rect),
            Frame = Frame,
            RealtimeMs = RealtimeMs,
        };
    }

    [RpcMethod(Methods.OverlayInvoke)]
    public ProtocolMessage Invoke(RequestContext context, OverlayInvokeParams p)
    {
        var automation = Automation();
        context.Target = "overlay:" + p.Element;
        string command;
        try
        {
            command = automation.Invoke(p.Element, p.Value);
        }
        catch (KeyNotFoundException)
        {
            throw new ProtocolException(ErrorCodes.NotFound, $"No overlay element '{p.Element}'.", Hint());
        }
        catch (InvalidOperationException e)
        {
            throw new ProtocolException(ErrorCodes.InvalidParams, e.Message);
        }

        return new OverlayInvokeResult { Invoked = true, Command = command, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.OverlayTypeText)]
    public ProtocolMessage TypeText(RequestContext context, OverlayTypeTextParams p)
    {
        var automation = Automation();
        OverlayElementState? element;
        if (p.Element is { } id)
        {
            element = Find(automation, id) ?? throw new ProtocolException(ErrorCodes.NotFound, $"No overlay element '{id}'.", Hint());
            if (element.Box is null)
            {
                throw new ProtocolException(ErrorCodes.InvalidParams, $"'{id}' isn't a text box.");
            }
        }
        else
        {
            element = _overlay.Keyboard.Focused is { } focused ? automation.Elements().FirstOrDefault(e => e.Box == focused) : null;
            if (element is null)
            {
                throw new ProtocolException(ErrorCodes.NotFound, "No overlay text box has the keyboard.", Hint("Name the text box (element), from overlay.snapshot."));
            }
        }

        context.Target = "overlay:" + element.Id;
        var box = element.Box!;
        _overlay.Keyboard.Focus(box);
        if (p.Replace == true)
        {
            box.Type(EditKeys.SelectAll.ToString());
        }

        // Line breaks are Shift+Enter (Enter alone would send).
        box.Paste(p.Text);
        var text = box.Text;
        var submitted = false;
        if (p.Submit == true)
        {
            box.Type("\n");
            submitted = true;
        }

        return new OverlayTypeTextResult { Element = element.Id, Text = text, Submitted = submitted, Frame = Frame, RealtimeMs = RealtimeMs };
    }

    [RpcMethod(Methods.OverlaySettings)]
    public ProtocolMessage Settings(RequestContext context, OverlaySettingsParams p) => new OverlaySettingsResult
    {
        Items = _overlay.SettingStates().Select(s => new OverlaySetting
        {
            Key = s.Key.Key,
            Name = s.Key.Title ?? s.Key.Name,
            Group = s.Key.Group ?? "Other",
            Description = s.Key.Help ?? s.Key.Description,
            Kind = s.Key.Kind.ToString().ToLowerInvariant(),
            Value = s.Value,
            Default = s.Key.Default,
            Saved = s.Saved,
            Choices = s.Key.Choices.ToList(),
            Min = s.Key.Min,
            Max = s.Key.Max,
            Step = s.Key.Kind == Hosting.ConfigKind.Float ? s.Key.Step : s.Key.Kind == Hosting.ConfigKind.Int ? 1 : null,
            AppliesNow = s.AppliesNow,
        }).ToList(),
    };

    [RpcMethod(Methods.OverlaySetSettings)]
    public ProtocolMessage SetSettings(RequestContext context, OverlaySetSettingsParams p)
    {
        var values = new List<KeyValuePair<string, string>>();
        foreach (var pair in p.Values)
        {
            values.Add(new KeyValuePair<string, string>(pair.Key, pair.Value is JsonString text ? text.Value : pair.Value?.ToString() ?? string.Empty));
        }

        var (applied, saved, restart, rejected) = _overlay.ChangeSettings(values, p.Persist == true);
        return new OverlaySetSettingsResult
        {
            Applied = applied,
            Saved = saved,
            RestartRequired = restart,
            Rejected = rejected.Select(r => new OverlaySettingRejection { Key = r.Key, Reason = r.Reason }).ToList(),
        };
    }

    private IOverlayAutomation Automation() =>
        _overlay.Automation ?? throw new ProtocolException(ErrorCodes.Unsupported, $"The overlay isn't drawn in this game ({_overlay.RendererReason ?? "no renderer"}).");

    private static OverlayElementState? Find(IOverlayAutomation automation, string id) => automation.Elements().FirstOrDefault(e => e.Id == id);

    private static JsonObject Hint(string hint = "Take an overlay.snapshot for the current ids.") => new() { { "hint", new JsonString(hint) } };

    private static OverlayElement Element(OverlayElementState e) => new()
    {
        Id = e.Id,
        Parent = e.Parent,
        Area = e.Area,
        Type = e.Type,
        Text = e.Text,
        Value = e.Value ?? JsonNull.Instance,
        Command = e.Command,
        Tooltip = e.Tooltip,
        Interaction = e.Interaction,
        Enabled = e.Enabled,
        Focused = e.Focused,
        Visibility = e.Visibility,
        Rect = Rect(e.Rect),
    };

    private static ScreenRect? Rect((double X, double Y, double W, double H)? rect) =>
        rect is { } r ? new ScreenRect { X = Math.Round(r.X, 1), Y = Math.Round(r.Y, 1), W = Math.Round(r.W, 1), H = Math.Round(r.H, 1) } : null;

    private sealed record OverlayPage(List<OverlayElementState> Elements, int Offset);
}
