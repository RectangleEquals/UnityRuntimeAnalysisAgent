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
    private double _scale = 1;
    private readonly Dictionary<string, RenderNode> _nodes = new(StringComparer.Ordinal);
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
    private IReadOnlyDictionary<string, ViewDocument> _views = new Dictionary<string, ViewDocument>();
    private Texture2D? _icons;
    private Dictionary<string, Rect> _iconRects = new(StringComparer.Ordinal);
    private Dictionary<string, Texture2D> _images = new(StringComparer.Ordinal);
    private string _signature = "";
    private bool _dirty = true;
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
        var signature = Signature(controller);
        if (!_dirty && signature == _signature)
        {
            return;
        }

        _signature = signature;
        _dirty = false;
        Rebuild(controller);
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

    private void OnRefreshed(string tab) => _dirty = true;

    private void OnModelChanged(OverlayModel model) => _dirty = true;

    // Everything that changes what's drawn, cheaply: redraw only when it changes.
    private string Signature(OverlayController c) => string.Join("|",
        Screen.width, Screen.height, c.Model.State, c.Model.Edge, c.Model.Offset.ToString("0.###", CultureInfo.InvariantCulture), c.Model.Docked, c.Model.Tab,
        c.EStop.Engaged, string.Join(",", c.Toasts.Visible.Select(t => t.Id + "x" + t.Count)), string.Join(",", c.Prompts.Pending.Select(p => p.Id)) + "|" + c.Prompts.Editing + "|" + c.Prompts.Draft + (c.Prompts.Editing is null ? string.Empty : PromptCard.CaretVisible ? "|on" : "|off"),
        _presenter?.Hovered, _presenter?.Pressed, _presenter?.Focused);

    private void Rebuild(OverlayController controller)
    {
        Rebuilds++;
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
        var model = controller.Model;
        if (model.State != OverlayVisibility.Hidden)
        {
            DrawArrow(controller, width, height);
            if (model.State == OverlayVisibility.Expanded)
            {
                DrawPanel(controller, width, height);
            }

            DrawCards(controller, width, height);
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
        var shell = Shell(controller);
        _presenter!.CheckedIds.Clear();
        _presenter.CheckedIds.Add("tab-" + model.Tab);
        var root = _presenter!.Present(shell, controller.Views.Data(model.Tab), panel.Width, panel.Height);
        Materialize(root, _root!.transform, panel.X, panel.Y, 0, 0);
        Occupy(panel.X, panel.Y, panel.Width, panel.Height);
    }

    private ViewDocument Shell(OverlayController controller)
    {
        var tabs = new ViewNode { Type = NodeType.Stack, Id = "tabs", Classes = { "tabs" } };
        tabs.Style["flex-direction"] = "row";
        tabs.Style["flex-wrap"] = "wrap";
        foreach (var tab in controller.Settings.VisibleTabs)
        {
            var button = new ViewNode { Type = NodeType.Text, Id = "tab-" + tab, Text = Title(tab), Classes = { "tab" }, Command = "tab.open", Args = new JsonObject { { "tab", new JsonString(tab) } } };
            tabs.Children.Add(button);
        }

        var header = new ViewNode { Type = NodeType.Stack, Id = "header" };
        header.Style["flex-direction"] = "row";
        header.Style["align-items"] = "center";
        header.Style["flex-shrink"] = "0";
        header.Children.Add(tabs);
        tabs.Style["flex-grow"] = "1";
        header.Children.Add(new ViewNode { Type = NodeType.Button, Id = "estop", Text = controller.EStop.Engaged ? "E-STOP ✓" : "E-STOP", Classes = { "danger" }, Command = "estop" });
        header.Children.Add(new ViewNode { Type = NodeType.Button, Id = "close", Text = "×", Command = "overlay.collapse" });
        var body = _views.TryGetValue(controller.Model.Tab, out var view)
            ? view.Root
            : new ViewNode { Type = NodeType.Text, Id = "missing", Text = $"No view for the '{controller.Model.Tab}' tab ({controller.Model.Tab}.json).", Classes = { "dim" } };
        var content = new ViewNode { Type = NodeType.Stack, Id = "content" };
        content.Style["flex-grow"] = "1";
        content.Style["overflow"] = "hidden";
        content.Style["margin-top"] = "$space.3";
        content.Children.Add(body);
        var panel = new ViewNode { Type = NodeType.Panel, Id = "panel" };
        panel.Style["width"] = "100%";
        panel.Style["height"] = "100%";
        panel.Children.Add(header);
        panel.Children.Add(content);
        return new ViewDocument("shell", panel, Array.Empty<string>());
    }

    // Toasts (always, next to the arrow) and prompt cards.
    private void DrawCards(OverlayController controller, double width, double height)
    {
        var stack = new ViewNode { Type = NodeType.Stack, Id = "cards" };
        stack.Style["position"] = "absolute";
        stack.Style["width"] = "360";
        foreach (var prompt in controller.Prompts.Pending)
        {
            stack.Children.Add(PromptCard.Build(prompt, controller.Prompts, Escape));
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

        var root = _presenter!.Present(new ViewDocument("cards", stack, Array.Empty<string>()), null, 360, double.NaN);
        var model = controller.Model;
        var arrow = EdgeDock.Arrow(model.Edge, model.Offset, Token("size", "arrow", 48), width, height);
        var x = model.Edge == OverlayEdge.Right ? arrow.X - root.Rect.Width - 8 : model.Edge == OverlayEdge.Left ? arrow.X + arrow.Width + 8 : Math.Max(0, Math.Min(arrow.X, width - root.Rect.Width));
        var y = model.Edge == OverlayEdge.Bottom ? arrow.Y - root.Rect.Height - 8 : model.Edge == OverlayEdge.Top ? arrow.Y + arrow.Height + 8 : Math.Max(0, Math.Min(arrow.Y, height - root.Rect.Height));
        Materialize(root, _root!.transform, x, y, 0, 0);
        Occupy(x, y, root.Rect.Width, root.Rect.Height);
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
        var style = node.Style;
        var radius = (int)Math.Round(style.BorderRadius);

        // Background (also the raycast target of interactive and clipping nodes).
        if ((style.BackgroundColor & 0xFF) != 0 || node.Interactive || node.Clips)
        {
            var image = Binder.Add(element.Go, Binder.Image);
            Binder.Set(image, "sprite", _sprites!.Get(radius, 0));
            Binder.Set(image, "type", "Sliced");
            Binder.Set(image, "color", EffectsDriver.Rgba(style.BackgroundColor));
            Binder.Set(image, "raycastTarget", node.Interactive || node.Clips);
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
            var size = Math.Min(Math.Min(w, h), 16);
            Place(layer, absX + (node.Source.Type == NodeType.Icon ? 0 : 6), absY + (h - size) / 2, size, size, absX, absY);
            var raw = Binder.Add(layer.Go, Binder.RawImage);
            Binder.Set(raw, "texture", _icons);
            Binder.Set(raw, "uvRect", uv);
            Binder.Set(raw, "color", EffectsDriver.Rgba(style.Color));
            Binder.Set(raw, "raycastTarget", false);
        }

        if (node.Source.Type == NodeType.Image && node.Source.Image is { } file && Image(file) is { } picture)
        {
            var raw = Binder.Add(element.Go, Binder.RawImage);
            Binder.Set(raw, "texture", picture);
            Binder.Set(raw, "raycastTarget", false);
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
            Binder.Add(element.Go, Binder.RectMask2D);
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

        foreach (var child in node.Children)
        {
            Materialize(child, element.Go.transform, originX, originY, absX, absY);
        }
    }

    private void DrawText(RenderNode node, Element element, string text, double x, double y, double w, double h)
    {
        var style = node.Style;
        var layer = Get(node.Path + "#text", element.Go.transform);
        var padding = style.Layout.Padding;
        var left = Zero(padding.Left.Resolve(w));
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
        Binder.Set(component, "alignment", (centered ? "Middle" : node.Source.Type is NodeType.Toggle or NodeType.TextField or NodeType.Dropdown ? "Middle" : "Upper") + style.TextAlign switch
        {
            "center" => "Center",
            "right" => "Right",
            _ => centered ? "Center" : "Left",
        });
        Binder.Set(component, "horizontalOverflow", style.NoWrap ? "Overflow" : "Wrap");
        Binder.Set(component, "verticalOverflow", "Truncate");
        Binder.Set(component, "text", style.Ellipsis ? Ellipsize(text, style, w) : text);
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
        Binder.On(go, "PointerEnter", _ => Set(() => _presenter!.Hovered = path));
        Binder.On(go, "PointerExit", _ => Set(() =>
        {
            if (_presenter!.Hovered == path)
            {
                _presenter.Hovered = null;
            }
        }));
        Binder.On(go, "PointerDown", _ => Set(() => _presenter!.Pressed = _presenter.Focused = path));
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
            if (_nodes.TryGetValue(path, out var node) && node.Clips)
            {
                _presenter!.ScrollBy(path, -e.Scroll.y * 24);
                _dirty = true;
            }
        });
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
        // The legacy Input class is read by reflection (D-001: it moved to its own module in 2019.1). In games set to
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
