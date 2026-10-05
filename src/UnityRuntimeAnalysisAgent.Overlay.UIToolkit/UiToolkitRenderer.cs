using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Overlay.Runtime;
using UnityRuntimeAnalysisAgent.Overlay.Views;
using Length = UnityEngine.UIElements.Length;
using LayoutAlign = UnityRuntimeAnalysisAgent.Overlay.Layout.Align;
using LayoutLength = UnityRuntimeAnalysisAgent.Overlay.Layout.Length;
using LayoutStyle = UnityRuntimeAnalysisAgent.Overlay.Layout.LayoutStyle;

namespace UnityRuntimeAnalysisAgent.Overlay.UIToolkit;

/// <summary>
/// The UI Toolkit renderer: a panel (PanelSettings made in code with the bundle's theme, sorted above the game's panels
/// and canvases) whose views are real VisualElements laid out by UI Toolkit, with its built-in controls (Button, Toggle,
/// Slider, DropdownField, TextField, ListView). Every visual property comes from our theme as inline style (fonts, text
/// colours and alignment, backgrounds, borders); rows never shrink and scroll views clip (D-012). Theme states (hover,
/// focus, press) follow pointer and focus events. A probe checks in the first frames that the theme really applies
/// (UI Toolkit doesn't report a theme it can't read); if not, <see cref="Failure"/> hands over to the uGUI renderer.
/// </summary>
public sealed class UiToolkitRenderer : IOverlayRenderer
{
    private readonly Dictionary<string, FontAsset> _fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2> _scroll = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _tabs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<OverlayEdge, Texture2D> _arrows = new();
    private readonly List<Rect> _occupied = new();
    private OverlayContext? _context;
    private GameObject? _host;
    private UIDocument? _document;
    private PanelSettings? _settings;
    private VisualElement? _probe;
    private VisualElement? _layer;
    private EffectsDriver? _effects;
    private OverlayCommands? _commands;
    private IReadOnlyDictionary<string, ViewDocument> _views = new Dictionary<string, ViewDocument>();
    private Texture2D? _icons;
    private readonly Dictionary<string, Sprite> _iconSprites = new(StringComparer.Ordinal);
    private string _signature = "";
    private bool _dirty = true;
    private int _frames;
    private bool _dragging;
    private Vector2 _dragStart;

    /// <inheritdoc />
    public string Name => "uitoolkit";

    /// <inheritdoc />
    public bool Alive => _host != null && _document != null;

    /// <inheritdoc />
    public string? Failure { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<Rect> Occupied
    {
        get
        {
            _occupied.Clear();
            if (_layer is not null && _settings != null)
            {
                var scale = _settings.scale;
                foreach (var child in _layer.Children())
                {
                    var r = child.worldBound;
                    if (!float.IsNaN(r.width) && r.width > 0)
                    {
                        _occupied.Add(new Rect(r.x * scale, r.y * scale, r.width * scale, r.height * scale));
                    }
                }
            }

            return _occupied;
        }
    }

    /// <summary>How many times the overlay was rebuilt (for the performance counters).</summary>
    public long Rebuilds { get; private set; }

    /// <inheritdoc />
    public bool TryStart(OverlayContext context, out string? reason)
    {
        var bundle = context.Bundle;
        var theme = bundle?.OfType(typeof(ThemeStyleSheet).FullName!) as ThemeStyleSheet;
        if (theme == null)
        {
            reason = "The overlay bundle has no UI Toolkit theme.";
            return false;
        }

        _context = context;
        _settings = ScriptableObject.CreateInstance<PanelSettings>();
        _settings.hideFlags = HideFlags.HideAndDontSave;
        _settings.themeStyleSheet = theme;
        _settings.scaleMode = PanelScaleMode.ConstantPixelSize;
        _settings.sortingOrder = short.MaxValue; // above the game's panels and canvases (the click blocker sits just below)
        _host = new GameObject("UnityRuntimeAnalysisAgent overlay (UI Toolkit)");
        UnityEngine.Object.DontDestroyOnLoad(_host);
        _host.AddComponent<UnityRuntimeAnalysisAgent.Unity.AgentOwned>();
        _document = _host.AddComponent<UIDocument>();
        _document.panelSettings = _settings;
        _effects = new EffectsDriver(context.Theme, bundle, context.Controller.Settings.Effects);
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
        if (_context is null || _document == null)
        {
            return;
        }

        var root = _document.rootVisualElement;
        if (root is null)
        {
            return;
        }

        if (_layer is null)
        {
            _layer = new VisualElement { name = "ov-layer", pickingMode = PickingMode.Ignore };
            _layer.style.position = Position.Absolute;
            _layer.style.left = _layer.style.top = _layer.style.right = _layer.style.bottom = 0;
            root.Add(_layer);
            _probe = new VisualElement { name = "ov-probe", pickingMode = PickingMode.Ignore };
            _probe.AddToClassList("ov-probe");
            _probe.style.position = Position.Absolute;
            root.Add(_probe);
        }

        // The self-check: the theme's probe rule must resolve (it doesn't when Unity can't read the theme).
        if (_probe is not null && ++_frames == 5)
        {
            if (Math.Abs(_probe.resolvedStyle.borderTopWidth - 3) > 0.01f)
            {
                Failure = "UI Toolkit's theme didn't apply in this Unity version (the overlay bundle's style sheets can't be read).";
                return;
            }

            _probe.RemoveFromHierarchy();
            _probe = null;
        }

        var controller = _context.Controller;
        _effects?.Tick(_context.Now());
        var settings = controller.Settings;
        var scale = settings.EffectiveScale(Screen.height);
        if (settings.RetroFonts)
        {
            scale = Math.Max(1, Math.Round(scale));
        }

        if (Math.Abs(_settings!.scale - (float)scale) > 0.001f)
        {
            _settings.scale = (float)scale;
            _dirty = true;
        }

        var signature = Signature(controller);
        var typing = root.focusController?.focusedElement is TextField;
        if ((!_dirty && signature == _signature) || typing)
        {
            return;
        }

        _signature = signature;
        _dirty = false;
        Rebuild(controller, Screen.width / scale, Screen.height / scale);
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_context is not null)
        {
            _context.Controller.Views.Refreshed -= OnRefreshed;
            _context.Controller.Model.Changed -= OnModelChanged;
        }

        if (_host != null)
        {
            UnityEngine.Object.Destroy(_host);
        }

        if (_settings != null)
        {
            UnityEngine.Object.Destroy(_settings);
        }

        foreach (var texture in _arrows.Values)
        {
            UnityEngine.Object.Destroy(texture);
        }

        foreach (var sprite in _iconSprites.Values)
        {
            UnityEngine.Object.Destroy(sprite);
        }

        if (_icons != null)
        {
            UnityEngine.Object.Destroy(_icons);
        }

        _effects?.Dispose();
        _host = null;
        _document = null;
        _layer = null;
    }

    private void OnRefreshed(string tab) => _dirty = true;

    private void OnModelChanged(OverlayModel model) => _dirty = true;

    private static string Signature(OverlayController c) => string.Join("|",
        Screen.width, Screen.height, c.Model.State, c.Model.Edge, c.Model.Offset.ToString("0.###", CultureInfo.InvariantCulture), c.Model.Docked, c.Model.Tab,
        c.EStop.Engaged, OverlayArrow.Status(c), string.Join(",", c.Toasts.Visible.Select(t => t.Id + "x" + t.Count)), string.Join(",", c.Prompts.Pending.Select(p => p.Id)) + "|" + c.Prompts.Editing + "|" + c.Prompts.Draft + (c.Prompts.Editing is null ? string.Empty : PromptCard.CaretVisible ? "|on" : "|off"));

    private void Rebuild(OverlayController controller, double width, double height)
    {
        Rebuilds++;
        SaveScroll(_layer!);
        _layer!.Clear();
        var model = controller.Model;
        if (model.State == OverlayVisibility.Hidden)
        {
            return;
        }

        var arrowSize = Number(_context!.Theme.Token("$size.arrow", "renderer"), 48);
        var arrow = EdgeDock.Arrow(model.Edge, model.Offset, arrowSize, width, height);
        _layer.Add(Arrow(controller, arrow));
        if (OverlayArrow.Badge(controller) is { } badgeNode)
        {
            // Placed once UI Toolkit has measured it.
            var badge = Build(badgeNode, "badge", null, null, null);
            badge.style.position = UnityEngine.UIElements.Position.Absolute;
            badge.pickingMode = PickingMode.Ignore;
            badge.style.visibility = Visibility.Hidden;
            badge.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var (bx, by) = OverlayArrow.BadgePosition(arrow, model.Edge, badge.layout.width, badge.layout.height, width, height);
                badge.style.left = (float)bx;
                badge.style.top = (float)by;
                badge.style.visibility = Visibility.Visible;
            });
            _layer.Add(badge);
        }
        if (model.State == OverlayVisibility.Expanded)
        {
            var (pw, ph) = controller.Settings.PanelSize is { } size ? ((double)size.Width, (double)size.Height) : (Math.Min(560, width * 0.45), Math.Min(720, height * 0.85));
            var rect = model.Docked ? EdgeDock.Panel(model.Edge, model.Offset, pw, ph, width, height, arrowSize)
                : new OverlayRect(Math.Max(0, Math.Min(model.FloatingPosition.X, width - pw)), Math.Max(0, Math.Min(model.FloatingPosition.Y, height - ph)), pw, ph);
            var panel = Shell(controller);
            Place(panel, rect.X, rect.Y, rect.Width, rect.Height);
            _layer.Add(panel);
        }

        var cards = Cards(controller);
        if (cards is not null)
        {
            var x = model.Edge == OverlayEdge.Right ? arrow.X - 368 : model.Edge == OverlayEdge.Left ? arrow.X + arrow.Width + 8 : Math.Max(0, Math.Min(arrow.X, width - 360));
            cards.style.position = UnityEngine.UIElements.Position.Absolute;
            cards.style.left = (float)x;
            if (model.Edge == OverlayEdge.Bottom)
            {
                // Above the arrow, anchored by its bottom: new cards appear nearest the arrow and push older ones up
                // (anchored by its top, the stack grew down off the screen).
                cards.style.bottom = (float)Math.Max(0, height - (arrow.Y - 8));
            }
            else
            {
                cards.style.top = (float)(model.Edge == OverlayEdge.Top ? arrow.Y + arrow.Height + 8 : Math.Max(0, Math.Min(arrow.Y, height - 200)));
            }

            cards.style.width = 360;
            _layer.Add(cards);
        }

        RestoreScroll(_layer);
    }

    private VisualElement Arrow(OverlayController controller, OverlayRect rect)
    {
        var model = controller.Model;
        if (!_arrows.TryGetValue(model.Edge, out var texture))
        {
            texture = ArrowTexture.Create(64, model.Edge);
            _arrows[model.Edge] = texture;
        }

        var arrow = new VisualElement { name = "ov-arrow" };
        Place(arrow, rect.X, rect.Y, rect.Width, rect.Height);
        arrow.style.backgroundImage = new StyleBackground(Background.FromTexture2D(texture));
        arrow.style.unityBackgroundImageTintColor = Color("$color." + OverlayArrow.Tint(OverlayArrow.Status(controller)));
        arrow.RegisterCallback<PointerDownEvent>(e =>
        {
            _dragging = false;
            _dragStart = e.position;
            arrow.CapturePointer(e.pointerId);
        });
        arrow.RegisterCallback<PointerMoveEvent>(e =>
        {
            if (arrow.HasPointerCapture(e.pointerId) && ((Vector2)e.position - _dragStart).sqrMagnitude > 16)
            {
                _dragging = true;
                var panel = arrow.panel?.visualTree.layout ?? default;
                controller.Model.Drag(e.position.x, e.position.y, panel.width, panel.height);
            }
        });
        arrow.RegisterCallback<PointerUpEvent>(e =>
        {
            arrow.ReleasePointer(e.pointerId);
            var panel = arrow.panel?.visualTree.layout ?? default;
            if (_dragging)
            {
                controller.Model.Drop(e.position.x, e.position.y, panel.width, panel.height);
            }
            else
            {
                controller.Model.Toggle();
            }

            _dragging = false;
        });
        return arrow;
    }

    // The panel: header (tabs, E-STOP, collapse) and the selected tab's view.
    private VisualElement Shell(OverlayController controller)
    {
        var tabs = new ViewNode { Type = NodeType.Stack, Id = "tabs", Classes = { "tabs" } };
        tabs.Style["flex-direction"] = "row";
        tabs.Style["flex-wrap"] = "wrap";
        tabs.Style["flex-grow"] = "1";
        foreach (var tab in controller.Settings.VisibleTabs)
        {
            var node = new ViewNode { Type = NodeType.Text, Id = "tab-" + tab, Text = Title(tab), Classes = { "tab" }, Command = "tab.open", Args = new JsonObject { { "tab", new JsonString(tab) } } };
            tabs.Children.Add(node);
        }

        var header = new ViewNode { Type = NodeType.Stack, Id = "header" };
        header.Style["flex-direction"] = "row";
        header.Style["align-items"] = "center";
        header.Style["flex-shrink"] = "0";
        header.Children.Add(tabs);
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
        panel.Children.Add(header);
        panel.Children.Add(content);
        var data = controller.Views.Data(controller.Model.Tab);
        var tabState = new Dictionary<string, NodeState>(StringComparer.Ordinal) { ["tab-" + controller.Model.Tab] = NodeState.Checked };
        return Build(panel, "shell", data, null, tabState);
    }

    private VisualElement? Cards(OverlayController controller)
    {
        if (controller.Prompts.Pending.Count == 0 && controller.Toasts.Visible.Count == 0)
        {
            return null;
        }

        var stack = new ViewNode { Type = NodeType.Stack, Id = "cards" };
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

        return Build(stack, "cards", null, null, null);
    }

    // A view node (and its subtree) as VisualElements, styled from the theme.
    private VisualElement Build(ViewNode node, string path, JsonValue? data, JsonValue? item, IReadOnlyDictionary<string, NodeState>? states)
    {
        var state = states is not null && node.Id is { } id && states.TryGetValue(id, out var s) ? s : NodeState.None;
        var text = node.Text is null ? null : Bindings.Text(node.Text, data, item);
        var value = node.Bind is null ? null : Bindings.Value(node.Bind, data, item);
        VisualElement element;
        switch (node.Type)
        {
            case NodeType.Text:
            case NodeType.Badge:
                element = new Label(text ?? string.Empty) { enableRichText = false };
                break;
            case NodeType.Button:
                element = new Button(() => Run(node, data, item)) { text = text ?? string.Empty, enableRichText = false };
                break;
            case NodeType.Toggle:
                var toggle = new Toggle(text ?? string.Empty);
                toggle.SetValueWithoutNotify(Bindings.Truthy(value));
                toggle.RegisterValueChangedCallback(e => Run(node, data, item, new JsonObject { { "value", e.newValue ? JsonBoolean.True : JsonBoolean.False } }));
                element = toggle;
                if (toggle.value)
                {
                    state |= NodeState.Checked;
                }

                break;
            case NodeType.Slider:
                var (min, max) = node.Range ?? (0, 1);
                var slider = new Slider((float)min, (float)max);
                slider.SetValueWithoutNotify(value is JsonNumber n ? (float)n.GetDouble() : (float)min);
                slider.RegisterValueChangedCallback(e => Run(node, data, item, new JsonObject { { "value", new JsonNumber(e.newValue) } }));
                element = slider;
                break;
            case NodeType.Dropdown:
                var choices = node.Items is not null && Bindings.Value(node.Items, data, item) is JsonArray array ? array.Select(Bindings.Plain).ToList() : new List<string>();
                var dropdown = new DropdownField(choices, Math.Max(0, choices.IndexOf(Bindings.Plain(value))));
                dropdown.RegisterValueChangedCallback(e => Run(node, data, item, new JsonObject { { "value", new JsonString(e.newValue) } }));
                element = dropdown;
                break;
            case NodeType.TextField:
                var field = new TextField { isDelayed = true };
                field.SetValueWithoutNotify(Bindings.Plain(value));
                field.RegisterValueChangedCallback(e => Run(node, data, item, new JsonObject { { "value", new JsonString(e.newValue) } }));
                element = field;
                break;
            case NodeType.List:
            case NodeType.Tree:
                element = List(node, path, data, item);
                break;
            case NodeType.Table:
                element = Table(node, path, data, item);
                break;
            case NodeType.Tabs:
                element = Tabs(node, path, data, item);
                break;
            case NodeType.Progress:
                element = new VisualElement();
                var fill = new VisualElement { pickingMode = PickingMode.Ignore };
                var fraction = value is JsonNumber p ? Math.Max(0, Math.Min(1, p.GetDouble())) : 0;
                fill.style.width = new Length((float)(fraction * 100), LengthUnit.Percent);
                fill.style.height = new Length(100, LengthUnit.Percent);
                element.Add(fill);
                break;
            case NodeType.Sparkline:
                element = Sparkline(value);
                break;
            case NodeType.Image:
                element = new VisualElement();
                if (node.Image is { } file && LoadTexture(Path.Combine(Path.Combine(_context!.OverlayDir, "images"), file)) is { } picture)
                {
                    element.style.backgroundImage = new StyleBackground(Background.FromTexture2D(picture));
                }

                break;
            case NodeType.Effect:
                element = new VisualElement();
                if (node.Effect is { } preset && _effects?.Texture(preset) is RenderTexture effect)
                {
                    element.style.backgroundImage = new StyleBackground(Background.FromRenderTexture(effect));
                }

                break;
            default:
                element = new VisualElement();
                break;
        }

        element.name = node.Id ?? string.Empty;
        Apply(element, node, state);
        if (node.Icon is { } icon && IconSprite(icon) is { } sprite)
        {
            var iconElement = new VisualElement { pickingMode = PickingMode.Ignore };
            iconElement.style.width = iconElement.style.height = 16;
            iconElement.style.flexShrink = 0;
            iconElement.style.backgroundImage = new StyleBackground(Background.FromSprite(sprite));
            iconElement.style.unityBackgroundImageTintColor = Rgba(_context!.Theme.Resolve(node, state).Color);
            element.Insert(0, iconElement);

            // A Button measures only its own text, so an icon inserted next to it spilled over its neighbours: the text
            // moves into a child label, and the button sizes to icon + label.
            if (element is Button button && button.text.Length > 0)
            {
                var label = new Label(button.text) { pickingMode = PickingMode.Ignore, enableRichText = false };
                label.style.marginLeft = 4;
                label.style.paddingLeft = label.style.paddingRight = label.style.paddingTop = label.style.paddingBottom = 0;
                label.style.marginTop = label.style.marginBottom = label.style.marginRight = 0;
                button.text = string.Empty;
                button.style.flexDirection = FlexDirection.Row;
                button.style.alignItems = Align.Center;
                button.Add(label);
            }
        }

        if (node.Type == NodeType.Progress && element.childCount > 0)
        {
            element[0].style.backgroundColor = Rgba(_context!.Theme.Resolve(node, state).Color);
        }

        if (node.Command is not null && node.Type is not (NodeType.Button or NodeType.Toggle or NodeType.Slider or NodeType.Dropdown or NodeType.TextField))
        {
            element.RegisterCallback<ClickEvent>(_ => Run(node, data, item));
        }

        if (node.Command is not null || node.Type is NodeType.Toggle or NodeType.TextField or NodeType.Dropdown or NodeType.Slider)
        {
            States(element, node, state);
        }

        if (node.Type is not (NodeType.List or NodeType.Tree or NodeType.Table or NodeType.Tabs))
        {
            var index = 0;
            foreach (var child in node.Children)
            {
                var childPath = path + "/" + (child.Id ?? index.ToString(CultureInfo.InvariantCulture));
                index++;
                if (Bindings.Visible(child.Visible, data, item))
                {
                    element.Add(Build(child, childPath, data, item, states));
                }
            }
        }

        return element;
    }

    // A virtualised list or tree: UI Toolkit's ListView, rows from the template, fixed height (or sized to their content
    // with wrapRows), never shrinking.
    private VisualElement List(ViewNode node, string path, JsonValue? data, JsonValue? item)
    {
        var rows = new List<(JsonValue Item, int Depth, string Path)>();
        if (node.Items is not null && Bindings.Value(node.Items, data, item) is { } items)
        {
            Flatten(rows, Items(items), node, path, 0);
        }

        var template = node.Template ?? new ViewNode { Type = NodeType.Text, Text = "{@}" };
        var rowHeight = Number(_context!.Theme.Token("$size.row", "renderer"), 20);
        var list = new ListView(rows, (float)rowHeight, () => new VisualElement(), (row, i) =>
        {
            row.Clear();
            var (rowItem, depth, rowPath) = rows[i];
            var content = Build(template, rowPath, data, rowItem, null);
            content.style.flexShrink = 0;
            content.style.paddingLeft = depth * 12;
            if (node.Type == NodeType.Tree && node.ChildrenPath is not null)
            {
                content.RegisterCallback<ClickEvent>(_ =>
                {
                    if (!_expanded.Remove(rowPath))
                    {
                        _expanded.Add(rowPath);
                    }

                    _dirty = true;
                });
            }
            else if (node.Command is not null)
            {
                content.RegisterCallback<ClickEvent>(_ => Run(node, data, rowItem));
            }

            row.Add(content);
        })
        {
            name = path,
            selectionType = SelectionType.None,
        };
        list.style.flexGrow = 1;
        list.style.overflow = Overflow.Hidden;
        if (node.WrapRows)
        {
            list.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight; // rows size to their (wrapped) text
        }

        return list;
    }

    private void Flatten(List<(JsonValue, int, string)> rows, IEnumerable<JsonValue> items, ViewNode node, string path, int depth)
    {
        var index = 0;
        foreach (var entry in items)
        {
            var rowPath = path + "/" + depth.ToString(CultureInfo.InvariantCulture) + "." + (index++).ToString(CultureInfo.InvariantCulture);
            rows.Add((entry, depth, rowPath));
            if (node.Type == NodeType.Tree && node.ChildrenPath is not null && _expanded.Contains(rowPath)
                && Bindings.Value("@." + node.ChildrenPath, null, entry) is { } children)
            {
                Flatten(rows, Items(children), node, rowPath, depth + 1);
            }
        }
    }

    private VisualElement Table(ViewNode node, string path, JsonValue? data, JsonValue? item)
    {
        var table = new VisualElement { name = path };
        table.style.flexGrow = 1;
        table.style.overflow = Overflow.Hidden;
        table.Add(TableRow(node, node.Columns.Select(c => c.Header).ToList(), "label"));
        var rows = node.Items is not null && Bindings.Value(node.Items, data, item) is { } items ? Items(items).ToList() : new List<JsonValue>();
        var rowHeight = Number(_context!.Theme.Token("$size.row", "renderer"), 20);
        var list = new ListView(rows, (float)rowHeight, () => new VisualElement(), (row, i) =>
        {
            row.Clear();
            var r = TableRow(node, node.Columns.Select(c => Bindings.Plain(Bindings.Value("@." + c.Bind, data, rows[i]))).ToList(), "value");
            if (node.Command is not null)
            {
                var rowItem = rows[i];
                r.RegisterCallback<ClickEvent>(_ => Run(node, data, rowItem));
            }

            row.Add(r);
        })
        {
            name = path + "/rows",
            selectionType = SelectionType.None,
        };
        list.style.flexGrow = 1;
        table.Add(list);
        return table;
    }

    private VisualElement TableRow(ViewNode table, IReadOnlyList<string> cells, string cellClass)
    {
        var row = Build(new ViewNode { Type = NodeType.Stack, Classes = { "row" } }, "row", null, null, null);
        row.style.flexDirection = FlexDirection.Row;
        row.style.flexShrink = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            var cellNode = new ViewNode { Type = NodeType.Text, Text = Escape(cells[i]), Classes = { cellClass } };
            cellNode.Style["white-space"] = "nowrap";
            cellNode.Style["text-overflow"] = "ellipsis";
            cellNode.Style["overflow"] = "hidden";
            var cell = Build(cellNode, "cell", null, null, null);
            if (table.Columns[i].Width is { } width && LayoutLength.TryParse(width, out var length))
            {
                cell.style.width = ToLength(length);
                cell.style.flexShrink = 0;
            }
            else
            {
                cell.style.flexGrow = 1;
                cell.style.flexBasis = 0;
            }

            row.Add(cell);
        }

        return row;
    }

    private VisualElement Tabs(ViewNode node, string path, JsonValue? data, JsonValue? item)
    {
        var visible = node.Children.Where(c => Bindings.Visible(c.Visible, data, item)).ToList();
        var container = new VisualElement { name = path };
        if (visible.Count == 0)
        {
            return container;
        }

        var selected = Math.Min(_tabs.TryGetValue(path, out var s) ? s : 0, visible.Count - 1);
        var header = new VisualElement();
        header.style.flexDirection = FlexDirection.Row;
        header.style.flexShrink = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            var index = i;
            var tab = new ViewNode { Type = NodeType.Text, Id = "tab" + i, Text = visible[i].Text ?? visible[i].Id ?? $"Tab {i + 1}", Classes = { "tab" } };
            var label = Build(tab, path + "/tab" + i, data, item, new Dictionary<string, NodeState> { ["tab" + selected] = NodeState.Checked });
            label.RegisterCallback<ClickEvent>(_ =>
            {
                _tabs[path] = index;
                _dirty = true;
            });
            header.Add(label);
        }

        container.Add(header);
        var content = Build(visible[selected], path + "/" + selected, data, item, null);
        content.style.flexGrow = 1;
        container.Add(content);
        return container;
    }

    private VisualElement Sparkline(JsonValue? value)
    {
        var line = new VisualElement();
        line.style.flexDirection = FlexDirection.Row;
        line.style.alignItems = Align.FlexEnd;
        var values = value is JsonArray array ? array.OfType<JsonNumber>().Select(n => n.GetDouble()).ToList() : new List<double>();
        var max = values.Count == 0 ? 1 : Math.Max(1e-9, values.Max());
        foreach (var v in values.Skip(Math.Max(0, values.Count - 60)))
        {
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.style.flexGrow = 1;
            bar.style.height = new Length((float)(Math.Max(0, v) / max * 100), LengthUnit.Percent);
            line.Add(bar);
        }

        return line;
    }

    // Theme states follow the pointer and focus: the resolved style for the new state is applied inline.
    private void States(VisualElement element, ViewNode node, NodeState baseState)
    {
        var hover = false;
        var active = false;
        var focus = false;
        void Refresh() => Apply(element, node, baseState | (hover ? NodeState.Hover : 0) | (active ? NodeState.Active : 0) | (focus ? NodeState.Focus : 0));
        element.RegisterCallback<PointerEnterEvent>(_ => { hover = true; Refresh(); });
        element.RegisterCallback<PointerLeaveEvent>(_ => { hover = active = false; Refresh(); });
        element.RegisterCallback<PointerDownEvent>(_ => { active = true; Refresh(); }, TrickleDown.TrickleDown);
        element.RegisterCallback<PointerUpEvent>(_ => { active = false; Refresh(); }, TrickleDown.TrickleDown);
        element.RegisterCallback<FocusInEvent>(_ => { focus = true; Refresh(); });
        element.RegisterCallback<FocusOutEvent>(_ => { focus = false; Refresh(); });
    }

    // Every look and layout property, inline (nothing left to Unity's defaults).
    private void Apply(VisualElement element, ViewNode node, NodeState state)
    {
        var resolved = _context!.Theme.Resolve(node, state);
        var layout = resolved.Layout;
        var s = element.style;
        s.flexDirection = layout.FlexDirection switch
        {
            Layout.FlexDirection.Row => FlexDirection.Row,
            Layout.FlexDirection.RowReverse => FlexDirection.RowReverse,
            Layout.FlexDirection.ColumnReverse => FlexDirection.ColumnReverse,
            _ => FlexDirection.Column,
        };
        s.flexWrap = layout.FlexWrap switch
        {
            Layout.FlexWrap.Wrap => Wrap.Wrap,
            Layout.FlexWrap.WrapReverse => Wrap.WrapReverse,
            _ => Wrap.NoWrap,
        };
        s.flexGrow = (float)layout.FlexGrow;
        s.flexShrink = (float)layout.FlexShrink;
        s.flexBasis = ToLength(layout.FlexBasis);
        s.alignItems = ToAlign(layout.AlignItems);
        s.alignSelf = ToAlign(layout.AlignSelf);
        s.alignContent = ToAlign(layout.AlignContent);
        s.justifyContent = layout.JustifyContent switch
        {
            Layout.Justify.Center => Justify.Center,
            Layout.Justify.FlexEnd => Justify.FlexEnd,
            Layout.Justify.SpaceBetween => Justify.SpaceBetween,
            Layout.Justify.SpaceAround => Justify.SpaceAround,
            _ => Justify.FlexStart,
        };
        s.position = layout.Position == Layout.PositionType.Absolute ? UnityEngine.UIElements.Position.Absolute : UnityEngine.UIElements.Position.Relative;
        s.left = ToLength(layout.Offsets.Left);
        s.top = ToLength(layout.Offsets.Top);
        s.right = ToLength(layout.Offsets.Right);
        s.bottom = ToLength(layout.Offsets.Bottom);
        s.width = ToLength(layout.Width);
        s.height = ToLength(layout.Height);
        s.minWidth = ToLength(layout.MinWidth);
        s.minHeight = ToLength(layout.MinHeight);
        s.maxWidth = ToLength(layout.MaxWidth);
        s.maxHeight = ToLength(layout.MaxHeight);
        s.marginLeft = Zeroed(layout.Margin.Left);
        s.marginTop = Zeroed(layout.Margin.Top);
        s.marginRight = Zeroed(layout.Margin.Right);
        s.marginBottom = Zeroed(layout.Margin.Bottom);
        s.paddingLeft = Zeroed(layout.Padding.Left);
        s.paddingTop = Zeroed(layout.Padding.Top);
        s.paddingRight = Zeroed(layout.Padding.Right);
        s.paddingBottom = Zeroed(layout.Padding.Bottom);
        s.borderLeftWidth = Pixels(layout.Border.Left);
        s.borderTopWidth = Pixels(layout.Border.Top);
        s.borderRightWidth = Pixels(layout.Border.Right);
        s.borderBottomWidth = Pixels(layout.Border.Bottom);
        s.display = layout.Display == Layout.Display.None ? DisplayStyle.None : DisplayStyle.Flex;
        s.overflow = layout.Overflow == Layout.Overflow.Hidden || resolved.Ellipsis ? Overflow.Hidden : Overflow.Visible;
        s.color = Rgba(resolved.Color);
        s.backgroundColor = Rgba(resolved.BackgroundColor);
        s.borderLeftColor = s.borderTopColor = s.borderRightColor = s.borderBottomColor = Rgba(resolved.BorderColor);
        var radius = (float)resolved.BorderRadius;
        s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = radius;
        s.opacity = (float)resolved.Opacity;
        var (font, size) = FontChoice.For(resolved.Font, resolved.FontSize, _context.Controller.Settings.RetroFonts && _context.Bundle is not null, 1);
        if (Font(font) is { } fontAsset)
        {
            s.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromSDFFont(fontAsset));
        }

        s.fontSize = (float)size;
        s.unityFontStyleAndWeight = resolved.FontStyle switch
        {
            "bold" => FontStyle.Bold,
            "italic" => FontStyle.Italic,
            "bold-and-italic" => FontStyle.BoldAndItalic,
            _ => FontStyle.Normal,
        };
        var vertical = node.Type is NodeType.Button or NodeType.Badge or NodeType.Toggle or NodeType.TextField or NodeType.Dropdown ? "Middle" : "Upper";
        s.unityTextAlign = (TextAnchor)Enum.Parse(typeof(TextAnchor), vertical + resolved.TextAlign switch
        {
            "center" => "Center",
            "right" => "Right",
            _ => node.Type is NodeType.Button or NodeType.Badge ? "Center" : "Left",
        });
        s.whiteSpace = resolved.NoWrap ? WhiteSpace.NoWrap : WhiteSpace.Normal;
        s.textOverflow = resolved.Ellipsis ? TextOverflow.Ellipsis : TextOverflow.Clip;
    }

    // Runtime SDF font assets from the bundle's fonts (D-011), made once per font.
    private FontAsset? Font(string name)
    {
        if (_fonts.TryGetValue(name, out var asset))
        {
            return asset;
        }

        if (_context!.Bundle?.Font(name) is not { } font)
        {
            return null;
        }

        asset = FontAsset.CreateFontAsset(font, 48, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
        if (asset != null)
        {
            asset.hideFlags = HideFlags.HideAndDontSave;
            asset.name = name + " SDF";
            _fonts[name] = asset;
        }

        return asset;
    }

    private void Run(ViewNode node, JsonValue? data, JsonValue? item, JsonObject? extra = null)
    {
        if (node.Command is null)
        {
            return;
        }

        var args = node.Args is not null ? Bindings.ResolveArgs(node.Args, data, item) : new JsonObject();
        if (node.Args is null && item is not null)
        {
            args.Set("item", item);
        }

        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                args.Set(pair.Key, pair.Value);
            }
        }

        _commands!.Run(node.Command, args);
        _dirty = true;
    }

    private void SaveScroll(VisualElement root) => root.Query<ScrollView>().ForEach(v =>
    {
        var key = v.parent?.name ?? v.name;
        if (!string.IsNullOrEmpty(key))
        {
            _scroll[key] = v.scrollOffset;
        }
    });

    private void RestoreScroll(VisualElement root) => root.schedule.Execute(() => root.Query<ScrollView>().ForEach(v =>
    {
        var key = v.parent?.name ?? v.name;
        if (!string.IsNullOrEmpty(key) && _scroll.TryGetValue(key, out var offset))
        {
            v.scrollOffset = offset;
        }
    }));

    private void LoadIcons(string overlayDir)
    {
        var png = Path.Combine(Path.Combine(overlayDir, "icons"), "phosphor.png");
        var map = Path.Combine(Path.Combine(overlayDir, "icons"), "phosphor.json");
        if (!File.Exists(png) || !File.Exists(map) || LoadTexture(png) is not { } texture)
        {
            return;
        }

        _icons = texture;
        var json = (JsonObject)JsonValue.Parse(File.ReadAllText(map));
        foreach (var icon in (JsonObject)json["icons"]!)
        {
            var r = ((JsonArray)icon.Value).Select(v => (float)((JsonNumber)v).GetDouble()).ToArray();
            var sprite = Sprite.Create(texture, new Rect(r[0], texture.height - r[1] - r[3], r[2], r[3]), new Vector2(0.5f, 0.5f), 100);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _iconSprites[icon.Key] = sprite;
        }
    }

    private Sprite? IconSprite(string name) => _iconSprites.TryGetValue(name, out var sprite) ? sprite : null;

    private static Texture2D? LoadTexture(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        return ImageConversion.LoadImage(texture, File.ReadAllBytes(path)) ? texture : null;
    }

    private static void Place(VisualElement element, double x, double y, double w, double h)
    {
        element.style.position = UnityEngine.UIElements.Position.Absolute;
        element.style.left = (float)x;
        element.style.top = (float)y;
        element.style.width = (float)w;
        element.style.height = (float)h;
    }

    private static StyleLength ToLength(LayoutLength length) => length.Unit switch
    {
        Layout.LengthUnit.Pixel => new StyleLength((float)length.Value),
        Layout.LengthUnit.Percent => new StyleLength(new Length((float)length.Value, LengthUnit.Percent)),
        Layout.LengthUnit.Auto => new StyleLength(StyleKeyword.Auto),
        _ => new StyleLength(StyleKeyword.Auto),
    };

    // Margins and padding: unset is 0 (never the default theme's), auto is auto.
    private static StyleLength Zeroed(LayoutLength length) => length.Unit == Layout.LengthUnit.Undefined ? new StyleLength(0f) : ToLength(length);

    private static StyleFloat Pixels(LayoutLength length) => length.Unit == Layout.LengthUnit.Pixel ? (float)length.Value : 0f;

    private static StyleEnum<Align> ToAlign(LayoutAlign align) => align switch
    {
        LayoutAlign.FlexStart => Align.FlexStart,
        LayoutAlign.Center => Align.Center,
        LayoutAlign.FlexEnd => Align.FlexEnd,
        LayoutAlign.Stretch => Align.Stretch,
        _ => Align.Auto,
    };

    private Color Color(string token) => _context!.Theme.Token(token, "renderer") is { } value && StyleValues.TryColor(value, out var rgba) ? Rgba(rgba) : UnityEngine.Color.white;

    private static Color Rgba(uint rgba) => EffectsDriver.Rgba(rgba);

    private static double Number(string? text, double fallback) => text is not null && StyleValues.TryNumber(text, out var n) ? n : fallback;

    private static IEnumerable<JsonValue> Items(JsonValue value) => value switch
    {
        JsonArray array => array,
        JsonObject o when o["items"] is JsonArray items => items,
        _ => Enumerable.Empty<JsonValue>(),
    };

    private static string Title(string tab) => tab == "mods" ? "Mods & Tests" : char.ToUpperInvariant(tab[0]) + tab.Substring(1);

    private static string Escape(string text) => text.Replace("{", "{{").Replace("}", "}}");
}
