using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Overlay.Layout;
using UnityRuntimeAnalysisAgent.Overlay.Runtime;
using UnityRuntimeAnalysisAgent.Overlay.Views;
using UnityRuntimeAnalysisAgent.Unity;

namespace UnityRuntimeAnalysisAgent.Overlay.Ugui;

/// <summary>
/// The styled uGUI renderer: an overlay canvas sorted above the game's, built in code. Views are laid out by the shared
/// presenter (our flex engine, the theme, scrolling lists that never squash or spill) and drawn with uGUI components
/// (rounded backgrounds and borders from generated sprites, text in the bundle's fonts, icons from the atlas, effects);
/// pointer input goes through the game's EventSystem (one is created if the game has none), which also keeps clicks on
/// the overlay from reaching the game's UI. Elements are pooled by path and redrawn only when something changed.
/// </summary>
public sealed class UguiRenderer : IOverlayRenderer
{
    private static readonly UguiOverlayBinder Binder = new();

    private readonly Dictionary<string, Element> _pool = new(StringComparer.Ordinal);
    private readonly List<Rect> _occupied = new();
    private readonly List<(object Text, GameObject Bar, int Index, double Since, GameObject[] Bands, (int Start, int End)? Selection)> _carets = new();
    private double _fieldWidth; // the prompt's text field as last laid out, for the prompt's own wrapping
    private readonly List<(Rect Rect, TextBox Box)> _textFields = new();
    private readonly Dictionary<GameObject, (object Text, TextBox Box, int ScrollBase)> _fieldTexts = new();
    private readonly HashSet<GameObject> _fieldWired = new();
    private (GameObject Go, Vector2 Screen)? _fieldDrag; // a drag in a text box, with the pointer's latest position
    private string _caretKey = string.Empty;
    private double _caretSince;
    private Func<string, double>? _fieldMeasure;
    private double _scale = 1;
    private readonly Dictionary<string, RenderNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double X, double Y, double W, double H)> _bounds = new(StringComparer.Ordinal); // where each node was drawn, in canvas units
    private readonly TooltipTimer _tooltip = new();
    private readonly List<ElementSource> _sources = new(); // what the last rebuild drew, for clients that drive the overlay
    private ViewNode? _shellHeader;
    private ViewNode? _shellBody;
    private ViewNode? _cardsNode;
    private string? _outlinePath;
    private double _outlineUntil;
    private readonly Dictionary<string, string> _cutTexts = new(StringComparer.Ordinal); // the whole texts of cut-off texts, by the path that shows them as tooltips
    private string? _hoverOwner; // the nearest interactive node being materialized
    private readonly HashSet<GameObject> _cutWired = new(); // cut-off texts that take the pointer themselves
    private readonly Dictionary<OverlayEdge, Texture2D> _arrows = new();
    private OverlayContext? _context;
    private GameObject? _root;
    private Canvas? _canvas;
    private Component? _scaler;
    private ViewPresenter? _presenter;
    private UguiFonts? _fonts;
    private UguiSprites? _sprites;
    private EffectsDriver? _effects;
    private OverlayCommands? _commands;
    private readonly TabStrip _tabStrip = new();
    private string? _tabRowPath; // the strip's row as last built
    private double _tabRowBuiltAt; // the row's scroll offset when it was built
    private bool _tabsGliding;
    private IReadOnlyDictionary<string, ViewDocument> _views = new Dictionary<string, ViewDocument>();
    private readonly Dictionary<string, IReadOnlyList<string>> _dependencies = new(StringComparer.Ordinal); // the data paths each tab's view shows
    private Texture2D? _icons;
    private Dictionary<string, Rect> _iconRects = new(StringComparer.Ordinal);
    private Dictionary<string, Texture2D> _images = new(StringComparer.Ordinal);
    private string _signature = "";
    private bool _dirty = true;
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);
    private bool _dragging;

    /// <inheritdoc />
    public string Name => "ugui";

    /// <inheritdoc />
    public bool Alive => _root != null && _canvas != null;

    /// <summary>How many times the overlay was rebuilt (for the performance counters: none while hidden and idle).</summary>
    public long Rebuilds { get; private set; }

    /// <inheritdoc />
    public string? Failure => null;

    /// <inheritdoc />
    public IReadOnlyList<Rect> Occupied => _occupied;

    /// <inheritdoc />
    public IReadOnlyList<(Rect Rect, TextBox Box)> TextFields => _textFields;

    /// <inheritdoc />
    public bool HandlesWheel => false;

    /// <inheritdoc />
    public bool TryStart(OverlayContext context, out string? reason)
    {
        if (!Binder.EnsureBound())
        {
            reason = Binder.Reason;
            return false;
        }

        _context = context;
        _root = new GameObject("UnityRuntimeAnalysisAgent overlay (uGUI)");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        _root.AddComponent<AgentOwned>();
        _canvas = _root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = short.MaxValue;
        _canvas.pixelPerfect = context.Controller.Settings.RetroFonts;
        _scaler = Binder.Add(_root, Binder.CanvasScaler);
        Binder.Add(_root, Binder.GraphicRaycaster);
        EnsureEventSystem();

        _fonts = new UguiFonts(context.Bundle, context.Controller.Settings.RetroFonts);
        _sprites = new UguiSprites();
        _effects = new EffectsDriver(context.Theme, context.Bundle, context.Controller.Settings.Effects);
        _presenter = new ViewPresenter(context.Theme, _fonts);
        _commands = new OverlayCommands(context.Controller, context.Log);
        _commands.Register(TabStrip.ScrollCommand, args => _tabStrip.Scroll(args["by"] is JsonNumber by ? (int)by.GetDouble() : 0));
        _views = OverlayFiles.LoadViews(context.OverlayDir, context.Log);
        LoadIcons(context.OverlayDir);
        context.Controller.Views.Refreshed += OnRefreshed;
        context.Controller.Model.Changed += OnModelChanged;
        reason = null;
        return true;
    }

    /// <inheritdoc />
    public void Update()
    {
        if (_context is null || _root == null)
        {
            return;
        }

        var controller = _context.Controller;
        var now = _context.Now();
        _effects?.Tick(now);
        DragTick();
        PlaceCarets(now);
        var signature = Signature(controller);
        if (_dirty || signature != _signature)
        {
            _signature = signature;
            _dirty = false;
            Rebuild(controller);
        }

        GlideTabs();
        PlaceOutline(now); // after the rebuild: it goes over what the rebuild drew
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_context is not null)
        {
            _context.Controller.Views.Refreshed -= OnRefreshed;
            _context.Controller.Model.Changed -= OnModelChanged;
        }

        if (_root != null)
        {
            UnityEngine.Object.Destroy(_root);
        }

        foreach (var texture in _arrows.Values.Concat(_images.Values).Concat(_icons is null ? Array.Empty<Texture2D>() : new[] { _icons }))
        {
            UnityEngine.Object.Destroy(texture);
        }

        _arrows.Clear();
        _images.Clear();
        _icons = null;
        _sprites?.Dispose();
        _effects?.Dispose();
        _pool.Clear();
        _root = null;
        _canvas = null;
    }

    // A tab's periodic refresh rebuilds the panel only when it shows that tab and a value its view shows changed
    // (ViewDependencies): a rebuild replaces the elements under the pointer (a click, a scroll position, a tooltip).
    private void OnRefreshed(string tab)
    {
        if (_context is null || tab != _context.Controller.Model.Tab)
        {
            return; // another tab: switching to it rebuilds anyway
        }

        var data = _context.Controller.Views.Data(tab);
        var key = _views.TryGetValue(tab, out var view)
            ? ViewDependencies.Key(_dependencies.TryGetValue(tab, out var paths) ? paths : _dependencies[tab] = ViewDependencies.Of(view), data)
            : data.ToString();
        if (_seen.TryGetValue(tab, out var previous) && previous == key)
        {
            return;
        }

        _seen[tab] = key;
        _dirty = true;
    }

    private void OnModelChanged(OverlayModel model) => _dirty = true;

    // Everything that changes what's drawn, cheaply: redraw only when it changes. Text boxes count by their edits
    // (Version) and the line their glide is at (the text doesn't slide here), and the keyboard focus.
    private string Signature(OverlayController c) => string.Join("|",
        Screen.width, Screen.height, c.Model.State, c.Model.Edge, c.Model.Offset.ToString("0.###", CultureInfo.InvariantCulture), c.Model.Docked, c.Model.Tab,
        c.EStop.Engaged, OverlayArrow.Status(c), string.Join(",", c.Toasts.Visible.Select(t => t.Id + "x" + t.Count)), string.Join(",", c.Prompts.Pending.Select(p => p.Field is { } box ? p.Id + ":" + box.Version : p.Id)),
        c.Keyboard.Version, string.Join(",", _textFields.Select(f => f.Box.Version + ":" + f.Box.ShownFirstLine).ToArray()),
        _presenter?.Hovered, _presenter?.Pressed, _presenter?.Focused, _tooltip.Shown(_context!.Now()), InputNoticeView.Signature(c.Input?.Notice));

    private void Rebuild(OverlayController controller)
    {
        Rebuilds++;
        _carets.Clear();
        _textFields.Clear();
        var settings = controller.Settings;
        var scale = settings.EffectiveScale(Screen.height);
        if (settings.RetroFonts)
        {
            scale = Math.Max(1, Math.Round(scale)); // whole factors keep the pixel font crisp
        }

        Binder.Set(_scaler!, "scaleFactor", (float)scale);
        _scale = scale;
        _occupied.Clear();
        var width = Screen.width / scale;
        var height = Screen.height / scale;
        foreach (var element in _pool.Values)
        {
            element.Used = false;
        }

        _nodes.Clear();
        _bounds.Clear();
        _sources.Clear();
        _cutTexts.Clear();
        var model = controller.Model;
        if (model.State != OverlayVisibility.Hidden)
        {
            DrawArrow(controller, width, height);
            if (model.State == OverlayVisibility.Expanded)
            {
                DrawPanel(controller, width, height);
            }

            DrawCards(controller, width, height);
            DrawTooltip(width, height);
        }

        DrawInputNotice(controller, width, height); // shown even while the overlay is hidden: it's a warning

        if (_pool.TryGetValue("#outline", out var outline))
        {
            outline.Used = true; // drawn every frame by PlaceOutline, not by rebuilds: it outlives them
        }

        foreach (var stale in _pool.Where(p => !p.Value.Used).ToList())
        {
            UnityEngine.Object.Destroy(stale.Value.Go);
            _pool.Remove(stale.Key);
        }
    }

    private void DrawArrow(OverlayController controller, double width, double height)
    {
        var model = controller.Model;
        var size = Token("size", "arrow", 48);
        var rect = EdgeDock.Arrow(model.Edge, model.Offset, size, width, height);
        var element = Get("arrow", _root!.transform);
        Occupy(rect.X, rect.Y, rect.Width, rect.Height);
        Place(element, rect.X, rect.Y, rect.Width, rect.Height, 0, 0);
        _bounds["arrow"] = (rect.X, rect.Y, rect.Width, rect.Height);
        _sources.Add(new ElementSource("arrow", "arrow", ArrowNode, "arrow", null));
        if (!_arrows.TryGetValue(model.Edge, out var texture))
        {
            texture = ArrowTexture.Create(64, model.Edge);
            _arrows[model.Edge] = texture;
        }

        var raw = Binder.Add(element.Go, Binder.RawImage);
        Binder.Set(raw, "texture", texture);
        Binder.Set(raw, "color", EffectsDriver.Rgba(Color(OverlayArrow.Tint(OverlayArrow.Status(controller)))));
        if (OverlayArrow.Badge(controller) is { } badge)
        {
            var node = _presenter!.Present(new ViewDocument("badge", badge, Array.Empty<string>()), null, double.NaN, double.NaN);
            var (bx, by) = OverlayArrow.BadgePosition(rect, model.Edge, node.Rect.Width, node.Rect.Height, width, height);
            Materialize(node, _root!.transform, bx, by, 0, 0);
        }
        if (!element.Wired)
        {
            element.Wired = true;
            Binder.On(element.Go, "PointerClick", _ =>
            {
                if (!_dragging)
                {
                    _context!.Controller.Model.Toggle();
                }
            });
            Binder.On(element.Go, "BeginDrag", _ => _dragging = true);
            Binder.On(element.Go, "Drag", e => Drag(e, drop: false));
            Binder.On(element.Go, "EndDrag", e =>
            {
                Drag(e, drop: true);
                _dragging = false;
            });
        }
    }

    private void Drag(UguiOverlayBinder.EventData e, bool drop)
    {
        var scale = Math.Max(0.01, Screen.height == 0 ? 1 : (double)(float)Binder.Get(_scaler!, "scaleFactor")!);
        var (x, y) = (e.Position.x / scale, (Screen.height - e.Position.y) / scale);
        var (w, h) = (Screen.width / scale, Screen.height / scale);
        if (drop)
        {
            _context!.Controller.Model.Drop(x, y, w, h);
        }
        else
        {
            _context!.Controller.Model.Drag(x, y, w, h);
        }
    }

    // The panel: a header with the visible tabs, collapse and E-STOP, then the selected tab's view.
    private void DrawPanel(OverlayController controller, double width, double height)
    {
        var model = controller.Model;
        var (pw, ph) = controller.Settings.PanelSize is { } size ? ((double)size.Width, (double)size.Height) : (Math.Min(560, width * 0.45), Math.Min(720, height * 0.85));
        var panel = model.Docked
            ? EdgeDock.Panel(model.Edge, model.Offset, pw, ph, width, height, Token("size", "arrow", 48))
            : new OverlayRect(Math.Max(0, Math.Min(model.FloatingPosition.X, width - pw)), Math.Max(0, Math.Min(model.FloatingPosition.Y, height - ph)), pw, ph);
        var shell = Shell(controller, panel.Width);
        _presenter!.CheckedIds.Clear();
        _presenter.CheckedIds.Add("tab-" + model.Tab);
        var data = controller.Views.Data(model.Tab);
        var root = _presenter!.Present(shell, data, panel.Width, panel.Height);
        Materialize(root, _root!.transform, panel.X, panel.Y, 0, 0);
        _tabRowPath = _tabStrip.Scrolling ? _nodes.Keys.FirstOrDefault(k => k.EndsWith("/" + TabStrip.RowId, StringComparison.Ordinal)) : null;
        _sources.Add(new ElementSource("header", "header", _shellHeader!, "shell/header", data));
        _sources.Add(new ElementSource("panel", "panel/" + model.Tab, _shellBody!, "shell/content/" + (_shellBody!.Id ?? "0"), data));
        Occupy(panel.X, panel.Y, panel.Width, panel.Height);
    }

    private ViewDocument Shell(OverlayController controller, double width)
    {
        var estop = new ViewNode { Type = NodeType.Button, Id = "estop", Text = controller.EStop.Engaged ? "E-STOP ✓" : "E-STOP", Classes = { "danger" }, Command = "estop" };
        estop.Style["margin-left"] = "$space.2";
        var close = new ViewNode { Type = NodeType.Button, Id = "close", Text = "×", Command = "overlay.collapse" };
        var panel = new ViewNode { Type = NodeType.Panel, Id = "panel" };
        var theme = _context!.Theme;
        var available = TabStrip.Available(width, panel, new[] { estop, close }, theme, TextWidth);
        var now = TextBox.Clock();
        var tabs = _tabStrip.Build(controller.Settings.VisibleTabs.Select(t => (t, Title(t))).ToList(), controller.Model.Tab, available, Screen.height, theme, TextWidth, now);
        _tabRowBuiltAt = _tabStrip.ShownAt(now);
        var header = new ViewNode { Type = NodeType.Stack, Id = "header" };
        header.Style["flex-direction"] = "row";
        header.Style["align-items"] = "center";
        header.Style["flex-shrink"] = "0";
        header.Children.Add(tabs);
        header.Children.Add(estop);
        header.Children.Add(close);
        var body = _views.TryGetValue(controller.Model.Tab, out var view)
            ? view.Root
            : new ViewNode { Type = NodeType.Text, Id = "missing", Text = $"No view for the '{controller.Model.Tab}' tab ({controller.Model.Tab}.json).", Classes = { "dim" } };
        PromptCard.Fill(body, controller.Prompts, Escape, _fieldWidth > 0 && _fieldMeasure is not null ? new FieldMetrics(_fieldWidth, _fieldMeasure) : null);
        var content = new ViewNode { Type = NodeType.Stack, Id = "content" };
        content.Style["flex-grow"] = "1";
        content.Style["overflow"] = "hidden";
        content.Style["margin-top"] = "$space.3";
        content.Children.Add(body);
        _shellHeader = header;
        _shellBody = body;
        panel.Style["width"] = "100%";
        panel.Style["height"] = "100%";
        panel.Children.Add(header);
        panel.Children.Add(content);
        return new ViewDocument("shell", panel, Array.Empty<string>());
    }

    // The input session's banner (top centre) and, while the assistant is in control, the frame around the screen. Neither
    // takes the pointer (no background raycasts: they aren't interactive).
    private void DrawInputNotice(OverlayController controller, double width, double height)
    {
        if (controller.Input?.Notice is not { } notice)
        {
            return;
        }

        if (InputNoticeView.Framed(notice))
        {
            var index = 0;
            foreach (var (x, y, barWidth, barHeight) in InputNoticeView.FrameBars(width, height))
            {
                var bar = InputNoticeView.Bar(index++);
                bar.Style["width"] = barWidth.ToString(CultureInfo.InvariantCulture);
                bar.Style["height"] = barHeight.ToString(CultureInfo.InvariantCulture);
                Materialize(_presenter!.Present(new ViewDocument(bar.Id!, bar, Array.Empty<string>()), null, barWidth, barHeight), _root!.transform, x, y, 0, 0);
            }
        }

        var w = Math.Min(InputNoticeView.MaxWidth, width - 32);
        var banner = _presenter!.Present(new ViewDocument("input-banner", InputNoticeView.Banner(notice, Escape), Array.Empty<string>()), null, w, double.NaN);
        Materialize(banner, _root!.transform, (width - banner.Rect.Width) / 2, 12, 0, 0);
    }

    // Toasts (always, next to the arrow) and prompt cards.
    private void DrawCards(OverlayController controller, double width, double height)
    {
        var stack = new ViewNode { Type = NodeType.Stack, Id = "cards" };
        stack.Style["position"] = "absolute";
        stack.Style["width"] = "360";
        // While the panel is open, prompts are answered in its Activity tab, not in cards on top of it.
        var prompts = controller.Model.State == OverlayVisibility.Expanded ? Array.Empty<Prompt>() : controller.Prompts.Pending;
        foreach (var prompt in prompts)
        {
            stack.Children.Add(PromptCard.Build(prompt, controller.Prompts, Escape, _fieldWidth > 0 && _fieldMeasure is not null ? new FieldMetrics(_fieldWidth, _fieldMeasure) : null));
        }

        foreach (var toast in controller.Toasts.Visible)
        {
            var level = toast.Level switch
            {
                ToastLevel.Error => "error",
                ToastLevel.Warning => "warn",
                ToastLevel.Success => "ok",
                _ => "text",
            };
            var node = new ViewNode { Type = NodeType.Panel, Id = "toast-" + toast.Id, Classes = { "toast" }, Command = "toast.dismiss", Args = new JsonObject { { "id", new JsonNumber(toast.Id) } } };
            node.Children.Add(new ViewNode { Type = NodeType.Text, Text = Escape(toast.Count > 1 ? $"{toast.Text} (×{toast.Count})" : toast.Text), Classes = { level } });
            stack.Children.Add(node);
        }

        if (stack.Children.Count == 0)
        {
            return;
        }

        _cardsNode = stack;
        _sources.Add(new ElementSource("cards", "cards", stack, "cards", null));
        var root = _presenter!.Present(new ViewDocument("cards", stack, Array.Empty<string>()), null, 360, double.NaN);
        var model = controller.Model;
        var arrow = EdgeDock.Arrow(model.Edge, model.Offset, Token("size", "arrow", 48), width, height);
        var x = model.Edge == OverlayEdge.Right ? arrow.X - root.Rect.Width - 8 : model.Edge == OverlayEdge.Left ? arrow.X + arrow.Width + 8 : Math.Max(0, Math.Min(arrow.X, width - root.Rect.Width));
        var y = model.Edge == OverlayEdge.Bottom ? arrow.Y - root.Rect.Height - 8 : model.Edge == OverlayEdge.Top ? arrow.Y + arrow.Height + 8 : Math.Max(0, Math.Min(arrow.Y, height - root.Rect.Height));
        Materialize(root, _root!.transform, x, y, 0, 0);
        Occupy(x, y, root.Rect.Width, root.Rect.Height);
    }

    // The tooltip of the node under the pointer, once the pointer rests on it: below the node (above it when there's no
    // room below), on screen, drawn last (over everything) and never taking the pointer.
    private void DrawTooltip(double width, double height)
    {
        if (_tooltip.Shown(_context!.Now()) is not { } text || _tooltip.Target is not string path || !_bounds.TryGetValue(path, out var over))
        {
            return;
        }

        var node = new ViewNode { Type = NodeType.Panel, Id = "tooltip", Classes = { "tooltip" } };
        node.Children.Add(new ViewNode { Type = NodeType.Text, Text = Escape(text), Classes = { "tooltip-text" } });
        var root = _presenter!.Present(new ViewDocument("tooltip", node, Array.Empty<string>()), null, Math.Min(320, width), double.NaN);
        var (w, h) = (root.Rect.Width, root.Rect.Height);
        var y = over.Y + over.H + 4;
        if (y + h > height && over.Y - h - 4 >= 0)
        {
            y = over.Y - h - 4;
        }

        Materialize(root, _root!.transform, Math.Max(0, Math.Min(over.X, width - w)), Math.Max(0, Math.Min(y, height - h)), 0, 0);
    }

    // ---- driving the overlay (OverlayAutomation) ------------------------------------------------------------------------

    // The arrow as an element: a click expands or collapses the panel.
    private static readonly ViewNode ArrowNode = new() { Type = NodeType.Button, Id = "arrow", Command = "overlay.toggle" };

    /// <inheritdoc />
    public IReadOnlyList<ElementSource> Sources => _sources;

    /// <inheritdoc />
    public ElementPlace Locate(string path, string? listPath, int rowIndex)
    {
        if (_bounds.TryGetValue(path, out var bounds))
        {
            var shown = bounds;
            foreach (var clip in ClippingAncestors(path))
            {
                shown = Intersect(shown, _bounds[clip]); // scrolled out of its list or clipped by its container
            }

            var onScreen = Intersect(shown, (0, 0, Screen.width / _scale, Screen.height / _scale));
            var visibility = shown.W < 0.5 || shown.H < 0.5 ? "clipped"
                : onScreen.W < 0.5 || onScreen.H < 0.5 ? "offscreen"
                : Math.Abs(onScreen.W - bounds.W) < 0.5 && Math.Abs(onScreen.H - bounds.H) < 0.5 ? "visible"
                : "partial";
            return new ElementPlace((bounds.X * _scale, bounds.Y * _scale, bounds.W * _scale, bounds.H * _scale), visibility);
        }

        // A list row that isn't built (lists build only the rows in view).
        return listPath is not null && _bounds.ContainsKey(listPath) ? new ElementPlace(null, "clipped") : new ElementPlace(null, "hidden");
    }

    /// <inheritdoc />
    public bool ScrollIntoView(string path, string? listPath, int rowIndex)
    {
        if (_dirty)
        {
            return true; // a scroll is waiting for its redraw: where things are now is out of date
        }

        if (_bounds.TryGetValue(path, out var bounds))
        {
            foreach (var clip in ClippingAncestors(path).Where(Scrolls))
            {
                var area = _bounds[clip];
                var delta = bounds.Y < area.Y ? bounds.Y - area.Y : bounds.Y + bounds.H > area.Y + area.H ? Math.Min(bounds.Y - area.Y, bounds.Y + bounds.H - (area.Y + area.H)) : 0;
                if (Math.Abs(delta) > 0.5)
                {
                    _presenter!.ScrollBy(clip, delta);
                    _dirty = true;
                }
            }

            return true;
        }

        if (listPath is not null && _bounds.ContainsKey(listPath))
        {
            _presenter!.ScrollToRow(listPath, rowIndex);
            _dirty = true;
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void Outline(string path, double seconds)
    {
        _outlinePath = path;
        _outlineUntil = (_context?.Now() ?? 0) + seconds;
    }

    /// <inheritdoc />
    public void Run(string command, JsonObject args)
    {
        _commands?.Run(command, args, _presenter);
        _dirty = true;
    }

    // Whether a clipping container scrolls (a list or table, or overflow: scroll); overflow: hidden only clips.
    private bool Scrolls(string path) =>
        _nodes.TryGetValue(path, out var node) && (node.Source.Type is NodeType.List or NodeType.Tree or NodeType.Table || node.Style.Layout.Overflow == Layout.Overflow.Scroll);

    // The containers that clip an element (scrolling lists, overflow hidden), innermost first.
    private IEnumerable<string> ClippingAncestors(string path)
    {
        for (var cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
        {
            var ancestor = path.Substring(0, cut);
            if (_nodes.TryGetValue(ancestor, out var node) && node.Clips && _bounds.ContainsKey(ancestor))
            {
                yield return ancestor;
            }
        }
    }

    private static (double X, double Y, double W, double H) Intersect((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        return (x, y, Math.Max(0, Math.Min(a.X + a.W, b.X + b.W) - x), Math.Max(0, Math.Min(a.Y + a.H, b.Y + b.H) - y));
    }

    // The outline around the element OverlayAutomation reveals, every frame until its time is up.
    private void PlaceOutline(double now)
    {
        var element = _pool.TryGetValue("#outline", out var pooled) ? pooled : null;
        if (_outlinePath is not { } path || now >= _outlineUntil || !_bounds.TryGetValue(path, out var bounds) || _root == null)
        {
            if (_outlinePath is not null && now >= _outlineUntil)
            {
                _outlinePath = null;
            }

            if (element is not null && element.Go != null)
            {
                element.Go.SetActive(false);
            }

            return;
        }

        element = Get("#outline", _root.transform);
        element.Go.SetActive(true);
        Place(element, bounds.X - 3, bounds.Y - 3, bounds.W + 6, bounds.H + 6, 0, 0);
        var image = Binder.Add(element.Go, Binder.Image);
        Binder.Set(image, "sprite", _sprites!.Get(2, 2));
        Binder.Set(image, "type", "Sliced");
        Binder.Set(image, "color", EffectsDriver.Rgba(Color("accent")));
        Binder.Set(image, "raycastTarget", false);
        element.Go.transform.SetAsLastSibling();
    }

    private void Occupy(double x, double y, double w, double h) =>
        _occupied.Add(new Rect((float)(x * _scale), (float)(y * _scale), (float)(w * _scale), (float)(h * _scale)));

    // Creates or updates the uGUI objects for a render node and its children (positions relative to the parent).
    private void Materialize(RenderNode node, Transform parent, double originX, double originY, double parentX, double parentY)
    {
        var (x, y, w, h) = node.Rect;
        var absX = originX + x;
        var absY = originY + y;
        var element = Get(node.Path, parent);
        Place(element, absX, absY, w, h, parentX, parentY);
        _nodes[node.Path] = node;
        _bounds[node.Path] = (absX, absY, w, h);
        var style = node.Style;
        var radius = (int)Math.Round(style.BorderRadius);

        // Background (also the raycast target of interactive and clipping nodes).
        if ((style.BackgroundColor & 0xFF) != 0 || node.Interactive || node.Clips)
        {
            var image = Binder.Add(element.Go, Binder.Image);
            Binder.Set(image, "enabled", true);
            Binder.Set(image, "sprite", _sprites!.Get(radius, 0));
            Binder.Set(image, "type", "Sliced");
            Binder.Set(image, "color", EffectsDriver.Rgba(style.BackgroundColor));
            Binder.Set(image, "raycastTarget", node.Interactive || node.Clips);
        }
        else if (element.Go.GetComponent(Binder.Image) is { } stale)
        {
            // Pooled by path: what was drawn here before (another tab, a hovered row, a rounded button) had a background;
            // this node has none, so the old one goes (it showed as stray bands and ovals).
            Binder.Set(stale, "enabled", false);
        }

        // Border ring.
        var borderWidth = (int)Math.Round(Zero(style.Layout.Border.Top.Resolve(0)));
        if (borderWidth > 0 && (style.BorderColor & 0xFF) != 0)
        {
            var ring = Get(node.Path + "#border", element.Go.transform);
            Place(ring, absX, absY, w, h, absX, absY);
            var image = Binder.Add(ring.Go, Binder.Image);
            Binder.Set(image, "sprite", _sprites!.Get(radius, borderWidth));
            Binder.Set(image, "type", "Sliced");
            Binder.Set(image, "color", EffectsDriver.Rgba(style.BorderColor));
            Binder.Set(image, "raycastTarget", false);
        }

        if (node.Source.Type == NodeType.Effect && node.Source.Effect is { } preset && _effects?.Texture(preset) is { } effect)
        {
            var layer = Get(node.Path + "#effect", element.Go.transform);
            Place(layer, absX, absY, w, h, absX, absY);
            var raw = Binder.Add(layer.Go, Binder.RawImage);
            Binder.Set(raw, "texture", effect);
            Binder.Set(raw, "raycastTarget", false);
        }

        if (node.Source.Icon is { } icon && _icons is not null && _iconRects.TryGetValue(icon, out var uv))
        {
            var layer = Get(node.Path + "#icon", element.Go.transform);
            var size = Math.Min(Math.Min(w, h), ViewPresenter.IconSize);
            var inset = node.Source.Type == NodeType.Icon ? 0 : Zero(style.Layout.Padding.Left.Resolve(w));
            Place(layer, absX + inset, absY + (h - size) / 2, size, size, absX, absY);
            var raw = Binder.Add(layer.Go, Binder.RawImage);
            Binder.Set(raw, "texture", _icons);
            Binder.Set(raw, "uvRect", uv);
            Binder.Set(raw, "color", EffectsDriver.Rgba(style.Color));
            Binder.Set(raw, "raycastTarget", false);
        }

        if (node.Source.Type == NodeType.Image && node.Source.Image is { } file && Image(file) is { } picture)
        {
            var raw = Binder.Add(element.Go, Binder.RawImage);
            Binder.Set(raw, "enabled", true);
            Binder.Set(raw, "texture", picture);
            Binder.Set(raw, "raycastTarget", false);
        }
        else if (element.Go.GetComponent(Binder.RawImage) is { } stalePicture)
        {
            Binder.Set(stalePicture, "enabled", false); // pooled: an image drawn here before
        }

        if (node.Source.Type == NodeType.Progress)
        {
            var fill = Get(node.Path + "#fill", element.Go.transform);
            var value = node.Value is JsonNumber n ? Math.Max(0, Math.Min(1, n.GetDouble())) : 0;
            Place(fill, absX, absY, w * value, h, absX, absY);
            var image = Binder.Add(fill.Go, Binder.Image);
            Binder.Set(image, "sprite", _sprites!.Get(radius, 0));
            Binder.Set(image, "type", "Sliced");
            Binder.Set(image, "color", EffectsDriver.Rgba(style.Color));
            Binder.Set(image, "raycastTarget", false);
        }

        var text = node.Text ?? (node.Source.Type is NodeType.TextField or NodeType.Dropdown ? Bindings.Plain(node.Value) : null);
        if (!string.IsNullOrEmpty(text))
        {
            DrawText(node, element, text!, absX, absY, w, h);
        }

        if (node.Clips)
        {
            Binder.Set(Binder.Add(element.Go, Binder.RectMask2D), "enabled", true);
        }
        else if (element.Go.GetComponent(Binder.RectMask2D) is { } staleMask)
        {
            Binder.Set(staleMask, "enabled", false); // pooled: a mask from what was here before cut this node's content off
        }

        if ((node.Interactive || node.Clips) && !element.Wired)
        {
            element.Wired = true;
            Wire(element.Go, node.Path);
        }

        var group = element.Go.GetComponent<CanvasGroup>() ?? (style.Opacity < 1 ? element.Go.AddComponent<CanvasGroup>() : null);
        if (group != null)
        {
            group.alpha = (float)style.Opacity;
        }

        var owner = _hoverOwner;
        if (node.Interactive)
        {
            _hoverOwner = node.Path;
        }

        foreach (var child in node.Children)
        {
            Materialize(child, element.Go.transform, originX, originY, absX, absY);
        }

        _hoverOwner = owner;
    }

    private void DrawText(RenderNode node, Element element, string text, double x, double y, double w, double h)
    {
        var style = node.Style;
        var layer = Get(node.Path + "#text", element.Go.transform);
        var padding = style.Layout.Padding;
        var left = Zero(padding.Left.Resolve(w)) + (node.Source.Icon is not null && node.Source.Type is not NodeType.Text ? ViewPresenter.IconSpace : 0);
        var top = Zero(padding.Top.Resolve(w));
        Place(layer, x + left, y + top, Math.Max(0, w - left - Zero(padding.Right.Resolve(w))), Math.Max(0, h - top - Zero(padding.Bottom.Resolve(w))), x, y);
        var (font, size) = _fonts!.For(style.Font, style.FontSize);
        var component = Binder.Add(layer.Go, Binder.Text);
        Binder.Set(component, "font", font);
        Binder.Set(component, "fontSize", size);
        Binder.Set(component, "supportRichText", false);
        Binder.Set(component, "color", EffectsDriver.Rgba(style.Color));
        Binder.Set(component, "raycastTarget", false);
        var centered = node.Source.Type is NodeType.Button or NodeType.Badge;
        var middle = centered || node.Source.Type is NodeType.Toggle or NodeType.TextField or NodeType.Dropdown || (node.Source.Type == NodeType.Text && node.Source.Command is not null); // a clickable text (a tab) is a control too
        Binder.Set(component, "alignment", (middle ? "Middle" : "Upper") + style.TextAlign switch
        {
            "center" => "Center",
            "right" => "Right",
            _ => centered ? "Center" : "Left",
        });
        Binder.Set(component, "horizontalOverflow", style.NoWrap ? "Overflow" : "Wrap");
        Binder.Set(component, "verticalOverflow", "Truncate");
        var shown = style.Ellipsis ? Ellipsize(text, style, w) : text;
        Binder.Set(component, "text", shown);
        if (shown != text && node.Tooltip is null)
        {
            if ((node.Interactive ? node.Path : _hoverOwner) is { } owner)
            {
                if (!(_nodes.TryGetValue(owner, out var ownerNode) && ownerNode.Tooltip is not null))
                {
                    _cutTexts[owner] = text;
                }
            }
            else
            {
                // Nothing to rest on: the text takes the pointer itself (it has no clicks to swallow).
                _cutTexts[node.Path] = text;
                Binder.Set(component, "raycastTarget", true);
                if (_cutWired.Add(layer.Go))
                {
                    Wire(layer.Go, node.Path);
                }
            }
        }

        if (node.Source.Box is { } textBox)
        {
            _textFields.Add((new Rect((float)(x * _scale), (float)(y * _scale), (float)(w * _scale), (float)(h * _scale)), textBox));

            // The mouse in the box: the text takes pointer events (once per pooled object; the handlers read the latest
            // build's text, box and scroll position for that object).
            _fieldTexts[layer.Go] = (component, textBox, node.Source.ScrollBase);
            Binder.Set(component, "raycastTarget", true);
            if (_fieldWired.Add(layer.Go))
            {
                var go = layer.Go;
                Binder.On(go, "PointerDown", d => FieldPointer(go, d.Position, down: true));
                Binder.On(go, "Drag", d => FieldPointer(go, d.Position, down: false));
                Binder.On(go, "PointerUp", _ => EndFieldDrag());
            }
        }

        if (node.Source.Caret is int caret)
        {
            var fieldWidth = Math.Max(0, w - left - Zero(padding.Right.Resolve(w)));
            _fieldMeasure = s => _fonts!.Measure(s, style.Font, style.FontSize, double.PositiveInfinity, true).Width;
            if (Math.Abs(fieldWidth - _fieldWidth) > 0.5)
            {
                _fieldWidth = fieldWidth; // the prompt wraps its text at this width from the next build
                _dirty = true;
            }

            var bar = Get(node.Path + "#caret", layer.Go.transform);
            var image = Binder.Add(bar.Go, Binder.Image);
            Binder.Set(image, "color", EffectsDriver.Rgba(style.Color));
            Binder.Set(image, "raycastTarget", false);
            // One selection band per line the field can show (placed in PlaceCarets).
            var bands = new GameObject[(node.Source.Box?.VisibleLines ?? 0) + 1];
            var tint = EffectsDriver.Rgba(style.Color);
            tint.a = 0.3f;
            for (var i = 0; i < bands.Length; i++)
            {
                var band = Get(node.Path + "#sel" + i, layer.Go.transform);
                var fill = Binder.Add(band.Go, Binder.Image);
                Binder.Set(fill, "color", tint);
                Binder.Set(fill, "raycastTarget", false);
                bands[i] = band.Go;
            }

            var key = text + "|" + caret + "|" + node.Source.Selection;
            if (key != _caretKey)
            {
                _caretKey = key; // the fade starts at the last edit or caret move, not at each rebuild
                _caretSince = _context!.Now();
            }

            _carets.Add((component, bar.Go, caret, _caretSince, bands, node.Source.Selection));
        }
    }

    // A press (or drag) in a text box: the line and column under the pointer, from the text's own generator (its lines'
    // tops and its characters' cursor positions, wrapping included), go to the box as a caret move; a drag or Shift
    // extends the selection, a double click selects the word (the box counts the clicks). A press gives the box the keyboard.
    private void FieldPointer(GameObject go, Vector2 screen, bool down, bool tick = false)
    {
        if (_context is null || !_fieldTexts.TryGetValue(go, out var field))
        {
            return;
        }

        var text = field.Text;

        var generator = text.GetType().GetProperty("cachedTextGenerator")?.GetValue(text, null) as TextGenerator;
        if (generator == null || generator.characterCount == 0 || generator.lineCount == 0)
        {
            return;
        }

        var unit = text.GetType().GetProperty("pixelsPerUnit")?.GetValue(text, null) is float p && p > 0 ? p : 1f;
        var local = go.transform.InverseTransformPoint(new Vector3(screen.x, screen.y, 0)) * unit; // an overlay canvas: world = screen
        var lines = generator.lines;
        var characters = generator.characters;
        var line = lines.Count - 1;
        for (var l = 0; l < lines.Count; l++)
        {
            if (local.y >= lines[l].topY - lines[l].height)
            {
                line = l;
                break;
            }
        }

        // The column nearest the pointer's x on one of the drawn lines.
        int Column(int drawn)
        {
            drawn = Math.Max(0, Math.Min(lines.Count - 1, drawn));
            var start = lines[drawn].startCharIdx;
            var end = Math.Min(characters.Count - 1, drawn + 1 < lines.Count ? lines[drawn + 1].startCharIdx : characters.Count - 1);
            var column = 0;
            var best = double.MaxValue;
            for (var i = start; i <= end; i++)
            {
                var distance = Math.Abs(characters[i].cursorPos.x - local.x);
                if (distance < best)
                {
                    best = distance;
                    column = i - start;
                }
            }

            return column;
        }

        // Past the drawn lines' top or bottom (in line heights), a drag scrolls the box toward the pointer.
        var height = Math.Max(1f, lines[0].height);
        var top = lines[0].topY;
        var bottom = lines[lines.Count - 1].topY - lines[lines.Count - 1].height;
        var beyond = local.y > top ? -(local.y - top) / height : local.y < bottom ? (bottom - local.y) / height : 0;
        if (down)
        {
            _context.Controller.Keyboard.Focus(field.Box);
            field.Box.Press(field.ScrollBase + line, Column(line), extend: PointerButtons.ShiftHeld());
            _fieldDrag = (go, screen);
        }
        else if (beyond == 0)
        {
            _fieldDrag = (go, screen);
            if (tick)
            {
                field.Box.DragBeyond(0, 0, _ => 0); // inside: only pointer moves select
                return;
            }

            field.Box.DragTo(field.ScrollBase + line, Column(line));
        }
        else
        {
            _fieldDrag = (go, screen);
            field.Box.DragBeyond(beyond, _context.Controller.Settings.DragScrollRate(beyond), l => Column(l - field.ScrollBase));
        }

        if (!tick)
        {
            _dirty = true; // a tick's changes show through the box's version (Signature)
        }
    }

    // A drag held past a text box's top or bottom keeps scrolling it every frame, moving or not.
    private void DragTick()
    {
        if (_fieldDrag is not { } drag)
        {
            return;
        }

        if (drag.Go == null || !PointerButtons.Held())
        {
            EndFieldDrag();
            return;
        }

        FieldPointer(drag.Go, drag.Screen, down: false, tick: true);
    }

    private void EndFieldDrag()
    {
        if (_fieldDrag is { } drag && _fieldTexts.TryGetValue(drag.Go, out var field))
        {
            field.Box.DragBeyond(0, 0, _ => 0);
        }

        _fieldDrag = null;
    }

    // Text fields' carets: placed at their character from the text's own generator (what Unity's input field uses: the
    // character's cursor position and its line's top and height, wrapping included), and blinking, every frame without
    // a rebuild. The text itself never moves.
    private void PlaceCarets(double now)
    {
        foreach (var (text, bar, index, since, bands, selection) in _carets)
        {
            if (bar == null)
            {
                continue;
            }

            var group = bar.GetComponent<CanvasGroup>() ?? bar.AddComponent<CanvasGroup>();
            var generator = text.GetType().GetProperty("cachedTextGenerator")?.GetValue(text, null) as TextGenerator;
            if (generator == null || generator.characterCount == 0 || generator.lineCount == 0)
            {
                group.alpha = 0; // not laid out yet
                continue;
            }

            var unit = text.GetType().GetProperty("pixelsPerUnit")?.GetValue(text, null) is float p && p > 0 ? p : 1f;
            var characters = generator.characters;
            var lines = generator.lines;
            var at = Math.Min(index, characters.Count - 1);
            var line = 0;
            for (var l = 0; l < lines.Count; l++)
            {
                if (lines[l].startCharIdx <= at)
                {
                    line = l;
                }
            }

            var rect = (RectTransform)bar.transform;
            rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = new Vector2(1, lines[line].height / unit);
            rect.localPosition = new Vector3(characters[at].cursorPos.x / unit, lines[line].topY / unit, 0);
            group.alpha = CaretLayout.Opacity(now - since);

            // The selection: a translucent band over each selected part of a line.
            for (var i = 0; i < bands.Length; i++)
            {
                if (bands[i] == null)
                {
                    continue;
                }

                var band = (RectTransform)bands[i].transform;
                band.pivot = new Vector2(0, 1);
                band.sizeDelta = Vector2.zero;
                if (selection is not { } s || i >= lines.Count)
                {
                    continue;
                }

                var lineStart = lines[i].startCharIdx;
                var lineEnd = i + 1 < lines.Count ? lines[i + 1].startCharIdx : characters.Count - 1;
                var from = Math.Max(s.Start, lineStart);
                var to = Math.Min(s.End, lineEnd);
                if (to <= from || from >= characters.Count)
                {
                    continue;
                }

                var last = characters[Math.Min(to, characters.Count) - 1];
                var x0 = characters[from].cursorPos.x;
                var x1 = Math.Max(x0 + 2 * unit, last.cursorPos.x + last.charWidth); // a selected line break shows as a sliver
                band.sizeDelta = new Vector2((x1 - x0) / unit, lines[i].height / unit);
                band.localPosition = new Vector3(x0 / unit, lines[i].topY / unit, 0);
            }
        }
    }

    private string Ellipsize(string text, ResolvedStyle style, double width)
    {
        if (_fonts!.Measure(text, style.Font, style.FontSize, double.PositiveInfinity, true).Width <= width)
        {
            return text;
        }

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_fonts.Measure(text.Substring(0, mid) + "…", style.Font, style.FontSize, double.PositiveInfinity, true).Width <= width)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return text.Substring(0, low) + "…";
    }

    // Pointer input for an element: hover, press, click (its command) and scrolling (clipping nodes).
    private void Wire(GameObject go, string path)
    {
        Binder.On(go, "PointerEnter", _ => Set(() =>
        {
            _presenter!.Hovered = path;
            if ((_nodes.TryGetValue(path, out var node) ? node.Tooltip : null) is { } tip || _cutTexts.TryGetValue(path, out tip))
            {
                _tooltip.Enter(path, tip, _context!.Now());
            }
        }));
        Binder.On(go, "PointerExit", _ => Set(() =>
        {
            _tooltip.Leave(path);
            if (_presenter!.Hovered == path)
            {
                _presenter.Hovered = null;
            }
        }));
        Binder.On(go, "PointerDown", _ => Set(() =>
        {
            _tooltip.Clear();
            _presenter!.Pressed = _presenter.Focused = path;
        }));
        Binder.On(go, "PointerUp", _ => Set(() => _presenter!.Pressed = null));
        Binder.On(go, "PointerClick", _ =>
        {
            if (_nodes.TryGetValue(path, out var node) && node.Command is { } command)
            {
                var args = node.Args;
                if (node.Source.Type == NodeType.Toggle)
                {
                    args = new JsonObject { { "value", Bindings.Truthy(node.Value) ? JsonBoolean.False : JsonBoolean.True } };
                }

                _commands!.Run(command, args, _presenter);
                _dirty = true;
            }
        });
        Binder.On(go, "Scroll", e =>
        {
            // The wheel scrolls the nearest container that scrolls: this element, or the list or view it's in (a row, a
            // button in a row: taking the pointer, they also take the wheel event, which doesn't go further by itself).
            var target = Scrolls(path) ? path : ClippingAncestors(path).FirstOrDefault(Scrolls); // (a button clips its text, but doesn't scroll)
            if (_tabStrip.Scrolling && path.IndexOf("/header/tabs/", StringComparison.Ordinal) >= 0)
            {
                // The tab strip moves a tab per notch (wheel down or right: on).
                if (_tabStrip.Scroll(e.Scroll.y < 0 || e.Scroll.x > 0 ? 1 : -1))
                {
                    _dirty = true;
                }
            }
            else if (target is not null)
            {
                _presenter!.ScrollBy(target, -e.Scroll.y * 24);
                _dirty = true;
            }
        });
    }

    // The tab strip's row glides between rebuilds: moved where it is rather than rebuilt; a rebuild once it settles
    // brings the arrows' dimming and the hit areas up to date.
    private void GlideTabs()
    {
        if (_tabRowPath is not { } path || !_tabStrip.Scrolling)
        {
            return;
        }

        var now = TextBox.Clock();
        var gliding = _tabStrip.Gliding(now);
        if (!gliding && !_tabsGliding)
        {
            return;
        }

        _tabsGliding = gliding;
        var parent = path.Substring(0, path.LastIndexOf('/'));
        if (_pool.TryGetValue(path, out var element) && element.Go != null && _bounds.TryGetValue(path, out var row) && _bounds.TryGetValue(parent, out var view))
        {
            Place(element, row.X + _tabRowBuiltAt - _tabStrip.ShownAt(now), row.Y, row.W, row.H, view.X, view.Y);
        }

        if (!gliding)
        {
            _dirty = true;
        }
    }

    // A text's width on one line in a node's font and size.
    private double TextWidth(ViewNode node, string text)
    {
        var style = _context!.Theme.Resolve(node);
        return _fonts!.Measure(text, style.Font, style.FontSize, double.PositiveInfinity, true).Width;
    }

    private void Set(Action change)
    {
        change();
        _dirty = true;
    }

    private Element Get(string path, Transform parent)
    {
        if (!_pool.TryGetValue(path, out var element) || element.Go == null)
        {
            var go = new GameObject(path, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            element = new Element(go);
            _pool[path] = element;
        }
        else if (element.Go.transform.parent != parent)
        {
            element.Go.transform.SetParent(parent, false);
        }

        element.Used = true;
        element.Go.transform.SetAsLastSibling();
        return element;
    }

    // Absolute rectangles (reference pixels, origin top left) to a child RectTransform of its parent.
    private static void Place(Element element, double x, double y, double w, double h, double parentX, double parentY)
    {
        var rt = (RectTransform)element.Go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2((float)(x - parentX), (float)-(y - parentY));
        rt.sizeDelta = new Vector2((float)w, (float)h);
    }

    private void EnsureEventSystem()
    {
        if (Binder.CurrentEventSystem != null)
        {
            return; // the game's own: its input module stays as it is
        }

        var go = new GameObject("UnityRuntimeAnalysisAgent EventSystem");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<AgentOwned>();
        Binder.Add(go, Binder.EventSystem);
        // The legacy Input class is read by reflection (it moved to its own module in 2019.1). In games set to
        // "Input System only" it throws, and the legacy module would throw every frame.
        var legacyInputWorks = true;
        var input = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule") ?? Type.GetType("UnityEngine.Input, UnityEngine.CoreModule") ?? Type.GetType("UnityEngine.Input, UnityEngine");
        try
        {
            _ = input?.GetProperty("mousePosition")?.GetValue(null, null);
        }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidOperationException)
        {
            legacyInputWorks = false;
        }

        Binder.Add(go, !legacyInputWorks && Binder.InputSystemUIInputModule is { } module ? module : Binder.StandaloneInputModule);
        go.transform.SetParent(_root!.transform, false);
    }

    private void LoadIcons(string overlayDir)
    {
        var png = Path.Combine(Path.Combine(overlayDir, "icons"), "phosphor.png");
        var map = Path.Combine(Path.Combine(overlayDir, "icons"), "phosphor.json");
        if (!File.Exists(png) || !File.Exists(map))
        {
            return;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(png)))
        {
            return;
        }

        _icons = texture;
        var json = (JsonObject)JsonValue.Parse(File.ReadAllText(map));
        foreach (var icon in (JsonObject)json["icons"]!)
        {
            var r = ((JsonArray)icon.Value).Select(v => (float)((JsonNumber)v).GetDouble()).ToArray();
            // Atlas rows go top-down in the map; textures are bottom-up.
            _iconRects[icon.Key] = new Rect(r[0] / texture.width, 1 - (r[1] + r[3]) / texture.height, r[2] / texture.width, r[3] / texture.height);
        }
    }

    private Texture2D? Image(string file)
    {
        if (_images.TryGetValue(file, out var texture))
        {
            return texture;
        }

        var path = Path.Combine(Path.Combine(_context!.OverlayDir, "images"), file);
        if (!File.Exists(path))
        {
            return null;
        }

        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        ImageConversion.LoadImage(texture, File.ReadAllBytes(path));
        _images[file] = texture;
        return texture;
    }

    private uint Color(string token) => _context!.Theme.Token("$color." + token, "arrow") is { } value && StyleValues.TryColor(value, out var rgba) ? rgba : 0xFFFFFFFF;

    private double Token(string group, string name, double fallback) =>
        _context!.Theme.Token($"${group}.{name}", "renderer") is { } value && StyleValues.TryNumber(value, out var number) ? number : fallback;

    private static string Title(string tab) => tab switch
    {
        "mods" => "Mods & Tests",
        _ => char.ToUpperInvariant(tab[0]) + tab.Substring(1),
    };

    private static string Escape(string text) => text.Replace("{", "{{").Replace("}", "}}");

    private static double Zero(double value) => double.IsNaN(value) ? 0 : value;

    private sealed class Element
    {
        public Element(GameObject go) => Go = go;

        public GameObject Go { get; }

        public bool Used { get; set; }

        public bool Wired { get; set; }
    }
}
