using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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
/// colours and alignment, backgrounds, borders); rows never shrink and scroll views clip. Theme states (hover,
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
    private readonly List<(VisualElement Bar, double Since)> _carets = new();
    private readonly TooltipTimer _tooltip = new();
    // In-place refreshes: when only the open tab's data changed, the elements built from it are updated where they are
    // (texts, values, list rows) instead of being replaced, so nothing under the pointer is lost (a click, a hover, a
    // scroll position, a tooltip). A change of structure (an element shown or hidden, other choices) still rebuilds.
    private readonly List<Func<JsonValue?, bool>> _updates = new(); // false: the change needs a rebuild
    private readonly Dictionary<VisualElement, string> _tipTexts = new(); // tooltips, as they last read
    private JsonValue? _builtData; // the tab data the last rebuild built from
    private JsonValue? _liveData; // the tab data now (in-place refreshes)
    private ViewNode? _shellNode;
    private bool _refresh; // the open tab's shown data changed: refresh in place
    private VisualElement? _arrowElement;
    private string? _arrowTint;
    private readonly List<ElementSource> _sources = new(); // what the last rebuild drew, for clients that drive the overlay
    private readonly Dictionary<string, VisualElement> _elements = new(StringComparer.Ordinal); // by path, as built
    private ViewNode? _shellHeader;
    private ViewNode? _shellBody;
    private ViewNode? _cardsNode;
    private VisualElement? _outline;
    private string? _outlinePath;
    private double _outlineUntil;
    private IReadOnlyDictionary<string, double> _groups = new Dictionary<string, double>(); // SizeGroups of the panel being built
    private readonly Dictionary<string, Label> _groupMeasures = new(StringComparer.Ordinal); // GroupTextWidth's labels, by font and size
    private readonly HashSet<Label> _measuresReady = new(); // those that have their font
    private readonly Dictionary<string, double> _unrestored = new(StringComparer.Ordinal); // scroll views not back at their saved position yet, since when
    private VisualElement? _tip; // the tooltip shown (on the root, over everything; it never takes the pointer)
    private string? _tipText;
    private Label? _measure; // hidden, styled like the text field: measures its lines for the prompt's wrapping
    // The prompts' text fields as last built: the box, the sheet that slides with the field's scroll every frame, the
    // prompt, and the scroll line the text starts at.
    private readonly List<(VisualElement Box, VisualElement Sheet, TextBox TextBox, int Base)> _fieldViews = new();
    private float _fieldAdvance; // their line height
    private string _caretKey = string.Empty;
    private double _caretSince;
    private bool _heldLastFrame;
    private float? _tabTarget; // where the tab's scroll view is easing to
    private ScrollView? _tabScroller; // the tab's scroll view as last built
    private IVisualElementScheduledItem? _tabTicker;
    private bool _restoring; // a rebuild's scroll positions aren't restored yet
    private double _fieldWidth;
    private OverlayContext? _context;
    private GameObject? _host;
    private UIDocument? _document;
    private PanelSettings? _settings;
    private VisualElement? _probe;
    private VisualElement? _layer;
    private EffectsDriver? _effects;
    private OverlayCommands? _commands;
    private IReadOnlyDictionary<string, ViewDocument> _views = new Dictionary<string, ViewDocument>();
    private readonly Dictionary<string, IReadOnlyList<string>> _dependencies = new(StringComparer.Ordinal); // the data paths each tab's view shows
    private Texture2D? _icons;
    private readonly Dictionary<string, Sprite> _iconSprites = new(StringComparer.Ordinal);
    private string _signature = "";
    private bool _dirty = true;
    private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);
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

    /// <inheritdoc />
    public bool HandlesWheel => true; // decided where the wheel event arrives (see CaretText and SmoothWheel)

    /// <inheritdoc />
    public IReadOnlyList<(Rect Rect, TextBox Box)> TextFields
    {
        get
        {
            var fields = new List<(Rect, TextBox)>();
            if (_settings == null)
            {
                return fields;
            }

            var scale = _settings.scale;
            foreach (var (box, _, textBox, _) in _fieldViews)
            {
                var r = box.worldBound;
                if (box.panel is not null && !float.IsNaN(r.width))
                {
                    fields.Add((new Rect(r.x * scale, r.y * scale, r.width * scale, r.height * scale), textBox));
                }
            }

            return fields;
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
        var now = TextBox.Clock();
        foreach (var (_, sheet, textBox, start) in _fieldViews)
        {
            // Each box's scroll, a fraction of a line at a time, every frame without a rebuild.
            if (sheet.panel is not null && _fieldAdvance > 0)
            {
                sheet.style.translate = new StyleTranslate(new Translate(0, -(float)((textBox.ShownLineAt(now) - start) * _fieldAdvance), 0));
            }
        }

        foreach (var (bar, since) in _carets)
        {
            bar.style.opacity = CaretLayout.Opacity(_context.Now() - since); // fades without a rebuild
        }

        PlaceOutline(root);
        TintArrow(controller);

        Tooltip(root);
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
        // Rebuilding replaces every element, so a press held across a rebuild never becomes a click (and hover resets).
        // A click needs its press and release on the same element: no rebuild while a mouse button is held over the
        // overlay (read from the mouse itself; a drag of the arrow still moves it).
        // ... and one more frame after the release, which UI Toolkit delivers after this rebuild would have replaced the
        // element under it (a lost click).
        var held = PointerButtons.Held();
        var holding = !_dragging && (held || _heldLastFrame);
        _heldLastFrame = held;
        if (typing || holding)
        {
            return;
        }

        if (!_dirty && signature == _signature)
        {
            if (!_refresh)
            {
                return;
            }

            _refresh = false;
            if (Refresh(controller))
            {
                return;
            }
        }

        _signature = signature;
        _dirty = false;
        _refresh = false;
        Rebuild(controller, Screen.width / scale, Screen.height / scale);
    }

    // The open tab's data changed and nothing else: updates the elements built from it where they are. False when the
    // change needs a rebuild (an element shown or hidden, a size group's width, other choices).
    private bool Refresh(OverlayController controller)
    {
        if (_builtData is null || _shellNode is null)
        {
            return true; // no panel: nothing shows the tab's data
        }

        var data = controller.Views.Data(controller.Model.Tab);
        var groups = SizeGroups.Measure(_shellNode, data, _context!.Theme, GroupTextWidth);
        if (groups.Count != _groups.Count || groups.Any(g => !_groups.TryGetValue(g.Key, out var width) || Math.Abs(width - g.Value) > 0.5))
        {
            return false;
        }

        _liveData = data;
        foreach (var update in _updates)
        {
            if (!update(data))
            {
                return false;
            }
        }

        // Clients that drive the overlay read the data too (OverlayAutomation).
        for (var i = 0; i < _sources.Count; i++)
        {
            if (_sources[i].Area is "panel" or "header")
            {
                _sources[i] = _sources[i] with { Data = data };
            }
        }

        return true;
    }

    // The arrow's colour follows the clients' activity every frame, in place (a client's every call used to rebuild the
    // whole panel, which lost its scroll positions and hover).
    private void TintArrow(OverlayController controller)
    {
        if (_arrowElement is null || _arrowElement.panel is null)
        {
            return;
        }

        var tint = OverlayArrow.Tint(OverlayArrow.Status(controller));
        if (tint != _arrowTint)
        {
            _arrowTint = tint;
            _arrowElement.style.unityBackgroundImageTintColor = Color("$color." + tint);
        }
    }

    // The data a handler should use now: the latest tab data in place of what the panel was built from.
    private JsonValue? Live(JsonValue? data) => data is not null && ReferenceEquals(data, _builtData) ? _liveData : data;

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
        _tabTicker = null;
        _tabScroller = null;
        _tabTarget = null;
    }

    // A text field's text with its caret drawn over it. The text is wrapped here, with the label's own measurement, so the
    // caret's line and column come from the same line breaks as the text on screen; the caret is a thin bar in the text's
    // colour (blinking in Update) that never moves the text.
    private VisualElement CaretText(string text, int? caret, ViewNode node)
    {
        if (_measure is null && _document != null)
        {
            _measure = new Label { enableRichText = false, pickingMode = PickingMode.Ignore };
            _measure.style.position = Position.Absolute;
            _measure.style.visibility = Visibility.Hidden;
            _document.rootVisualElement.Add(_measure);
        }

        if (_measure is not null)
        {
            Apply(_measure, node, NodeState.None); // the field's font, size and spacing
            _measure.style.position = Position.Absolute;
            _measure.style.visibility = Visibility.Hidden;
        }

        // box (the field's look and padding) > clip (the window of lines) > sheet (the text with its caret and selection,
        // slid every frame by the field's scroll, a fraction of a line at a time: see Update).
        var textBox = node.Box!;
        var box = new VisualElement();
        box.RegisterCallback<WheelEvent>(e =>
        {
            // The box's wheel: decided here, where the event arrives (see WheelLatch), so a gesture is never split. A box
            // with nothing to scroll leaves the wheel to what's around it (the tab).
            if (_context is null || !textBox.CanScroll)
            {
                return;
            }

            if (_context.Controller.Wheel.Claim(overBox: true, _context.Now()))
            {
                textBox.ScrollLines(e.delta.y > 0 ? 3 : -3);
                e.StopPropagation();
            }
            else if (box.GetFirstAncestorOfType<ScrollView>() is { } tab)
            {
                WheelTab(tab, e.delta.y); // a gesture that began on the tab keeps scrolling the tab
                e.StopPropagation();
            }
        });
        var clip = new VisualElement { pickingMode = PickingMode.Ignore };
        clip.style.overflow = Overflow.Hidden;
        var sheet = new VisualElement { pickingMode = PickingMode.Ignore };
        clip.Add(sheet);
        box.Add(clip);
        var label = new Label(text) { enableRichText = false, pickingMode = PickingMode.Ignore };
        label.style.whiteSpace = WhiteSpace.NoWrap;
        label.style.paddingLeft = label.style.paddingRight = label.style.paddingTop = label.style.paddingBottom = 0;
        label.style.marginLeft = label.style.marginRight = label.style.marginTop = label.style.marginBottom = 0;
        var bar = new VisualElement { name = "ov-caret", pickingMode = PickingMode.Ignore };
        bar.style.position = Position.Absolute;
        bar.style.width = 1;
        sheet.Add(label);
        sheet.Add(bar);
        var moved = false; // the text or the caret changed in this build (the tab then follows the caret)
        if (caret is null)
        {
            bar.style.display = DisplayStyle.None; // paused, or scrolled out of view: no caret
        }
        else
        {
            var key = RuntimeHelpers.GetHashCode(textBox) + "|" + text + "|" + caret + "|" + node.Selection;
            moved = key != _caretKey;
            _carets.Add((bar, CaretSince(key)));
        }

        _fieldViews.Add((box, sheet, textBox, node.ScrollBase));
        if (_fieldAdvance > 0)
        {
            sheet.style.translate = new StyleTranslate(new Translate(0, -(float)((textBox.ShownLineAt(TextBox.Clock()) - node.ScrollBase) * _fieldAdvance), 0));
        }
        // The layout of this build's text (once the field has a width), and the drawing of its caret and selection from
        // it: at layout, and again while the mouse places the caret or drags a selection (the panel doesn't rebuild
        // while a button is held, which would replace the field under the pointer).
        List<(int Start, int Length)>? lines = null;
        float height = 0;
        float advance = 0;
        var bands = new List<VisualElement>();
        Vector2 Size(string s) => label.MeasureTextSize(s, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);

        // Text measurement leaves out leading and trailing spaces: measure with a closing character, then take that
        // character off, so spaces count.
        float X(string lineText, int count) => count <= 0 ? 0 : Size(lineText.Substring(0, Math.Min(count, lineText.Length)) + "|").x - Size("|").x;

        void Draw(int? caretAt, (int Start, int End)? selection)
        {
            if (lines is null)
            {
                return;
            }

            if (caretAt is int at)
            {
                var (line, column) = CaretLayout.Locate(lines, at);
                bar.style.display = DisplayStyle.Flex;
                bar.style.left = X(CaretLayout.LineText(text, lines[line]), column);
                bar.style.top = line * advance;
                bar.style.height = height;
                bar.style.backgroundColor = label.resolvedStyle.color;
            }

            // The selection: a translucent band behind each selected part of a line.
            foreach (var old in bands)
            {
                old.RemoveFromHierarchy();
            }

            bands.Clear();
            if (selection is not { } chosen)
            {
                return;
            }

            var color = label.resolvedStyle.color;
            for (var i = 0; i < lines.Count; i++)
            {
                var lineText = CaretLayout.LineText(text, lines[i]);
                var from = Math.Max(chosen.Start, lines[i].Start) - lines[i].Start;
                var to = Math.Min(chosen.End, lines[i].Start + lineText.Length) - lines[i].Start;
                var throughBreak = chosen.Start <= lines[i].Start + lineText.Length && chosen.End > lines[i].Start + lineText.Length;
                if (to <= from && !throughBreak)
                {
                    continue;
                }

                var x0 = X(lineText, from);
                var x1 = Math.Max(x0, X(lineText, to)) + (throughBreak ? Size("|").x : 0); // a selected line break shows as a sliver
                var band = new VisualElement { pickingMode = PickingMode.Ignore };
                band.style.position = Position.Absolute;
                band.style.left = x0;
                band.style.top = i * advance;
                band.style.width = x1 - x0;
                band.style.height = height;
                band.style.backgroundColor = new Color(color.r, color.g, color.b, 0.3f);
                sheet.Insert(0, band); // behind the text
                bands.Add(band);
            }
        }

        var laidOut = -1f;
        box.RegisterCallback<GeometryChangedEvent>(_ =>
        {
            var width = box.contentRect.width;
            if (width <= 0 || Math.Abs(width - laidOut) < 0.5f)
            {
                return;
            }

            laidOut = width;
            if (Math.Abs(width - _fieldWidth) > 0.5f)
            {
                _fieldWidth = width; // the prompt wraps its text at this width from the next build
                _dirty = true;
            }

            lines = CaretLayout.Wrap(text, width, s => Size(s).x);
            label.text = string.Join("\n", lines.Select(l => CaretLayout.LineText(text, l)).ToArray());
            height = Size("Ag").y;
            advance = Size("Ag\nAg").y - height;
            _fieldAdvance = advance;
            label.style.minHeight = height + (lines.Count - 1) * advance; // every line counts, empty ones included
            if (node.Window is int window)
            {
                clip.style.height = height + (window - 1) * advance; // the window; the extra line slides in below it
            }

            Draw(caret, node.Selection);

            // After an edit, the tab keeps the field's window in view (the field's own scrolling keeps the caret inside it):
            // the caret itself isn't where it'll be until the field's glide catches up, and chasing it ran the tab off.
            if (moved && box.GetFirstAncestorOfType<ScrollView>() is { } scroller)
            {
                box.schedule.Execute(() => GlideIntoView(scroller, clip)); // after the restored scroll position
            }
        });

        // The mouse: a press places the caret (Shift extends the selection, a double click selects a word) and a drag
        // selects. The model follows at once; the drawing follows here until the release lets the panel rebuild (only
        // when the field's text starts at its first line, so its positions are the draft's).
        var dragging = false;
        (int Line, int Column) Hit(Vector2 position)
        {
            var local = sheet.WorldToLocal(position); // the sheet's slide included
            var line = Math.Max(0, Math.Min(lines!.Count - 1, (int)Math.Floor(local.y / Math.Max(1f, advance))));
            return (line + node.ScrollBase, Column(line, local.x));
        }

        // The column nearest an x on one of this build's lines.
        int Column(int line, float x)
        {
            var lineText = CaretLayout.LineText(text, lines![Math.Max(0, Math.Min(lines.Count - 1, line))]);
            var column = 0;
            var best = Math.Abs(x);
            for (var i = 1; i <= lineText.Length; i++)
            {
                var distance = Math.Abs(X(lineText, i) - x);
                if (distance < best)
                {
                    best = distance;
                    column = i;
                }
            }

            return column;
        }

        // How far past the window's top (negative) or bottom (positive) a point is, in line heights; 0 inside it.
        double Beyond(Vector2 position)
        {
            var window = clip.worldBound;
            var step = Math.Max(1f, advance);
            return position.y < window.yMin ? -(window.yMin - position.y) / step
                : position.y > window.yMax ? (position.y - window.yMax) / step
                : 0;
        }

        // A drag at a point: inside the window the selection follows the pointer; past its top or bottom the box scrolls
        // toward it (every frame while the pointer stays there: see the drag's ticker), selecting as it goes.
        var pointer = Vector2.zero;
        IVisualElementScheduledItem? ticker = null;
        void DragAt(Vector2 position)
        {
            pointer = position;
            var beyond = Beyond(position);
            if (beyond == 0)
            {
                var (line, column) = Hit(position);
                textBox.DragTo(line, column);
            }
            else
            {
                var x = sheet.WorldToLocal(position).x;
                textBox.DragBeyond(beyond, _context!.Controller.Settings.DragScrollRate(beyond), line => Column(line - node.ScrollBase, x));
            }

            Follow();
        }


        // A position in the box's text on this build's lines: the same line of the layout, the same column (the drawn text
        // has a line break where the box's text only wraps, so positions after a wrap differ).
        int ToShown(int position)
        {
            var starts = textBox.LineStarts;
            var line = 0;
            for (var i = 0; i < starts.Count && starts[i] <= position; i++)
            {
                line = i;
            }

            var shownLine = Math.Max(0, Math.Min(lines!.Count - 1, line - node.ScrollBase));
            return lines[shownLine].Start + Math.Max(0, Math.Min(position - starts[line], CaretLayout.LineText(text, lines[shownLine]).Length));
        }

        void Follow()
        {
            if (lines is not null && _context is not null)
            {
                Draw(ToShown(textBox.Caret), textBox.Selection is { } chosen ? (ToShown(chosen.Start), ToShown(chosen.End)) : null);
                var index = _carets.FindIndex(c => c.Bar == bar);
                if (index >= 0)
                {
                    _carets[index] = (bar, _context.Now()); // the caret shows at once where the mouse put it
                }
                else
                {
                    _carets.Add((bar, _context.Now()));
                }
            }
        }

        box.RegisterCallback<PointerDownEvent>(e =>
        {
            if (e.button != 0 || lines is null || _context is null)
            {
                return;
            }

            _context.Controller.Keyboard.Focus(textBox); // a press in a box gives it the keyboard
            var (line, column) = Hit(e.position);
            textBox.Press(line, column, extend: e.shiftKey); // the box counts double clicks (a redraw resets UI Toolkit's count)

            Follow();
            dragging = true;
            pointer = e.position;
            box.CapturePointer(e.pointerId);
            ticker?.Pause();
            ticker = box.schedule.Execute(() =>
            {
                if (dragging && Beyond(pointer) != 0)
                {
                    DragAt(pointer); // the pointer rests past the window: keep scrolling
                }
            }).Every(16);
            e.StopPropagation();
        });
        box.RegisterCallback<PointerMoveEvent>(e =>
        {
            if (!dragging || lines is null || _context is null || !box.HasPointerCapture(e.pointerId))
            {
                return;
            }

            DragAt(e.position);
        });
        void EndDrag()
        {
            dragging = false;
            ticker?.Pause();
            ticker = null;
            textBox.DragBeyond(0, 0, _ => 0);
        }

        box.RegisterCallback<PointerUpEvent>(e =>
        {
            if (dragging)
            {
                EndDrag();
                box.ReleasePointer(e.pointerId);
            }
        });
        box.RegisterCallback<PointerCaptureOutEvent>(_ =>
        {
            if (dragging)
            {
                EndDrag(); // the capture was taken away: the drag is over
            }
        });
        return box;
    }

    // The tooltip of the node under the pointer, once the pointer rests on it: below the node (above it when there's no
    // room below), kept on screen.
    private void Tooltip(VisualElement root)
    {
        var text = _tooltip.Shown(_context!.Now());
        if (text is null || _tooltip.Target is not VisualElement over || over.panel is null)
        {
            if (_tip is not null)
            {
                _tip.style.display = DisplayStyle.None;
            }

            return;
        }

        if (_tip is null || text != _tipText)
        {
            _tip?.RemoveFromHierarchy();
            var node = new ViewNode { Type = NodeType.Panel, Classes = { "tooltip" } };
            node.Children.Add(new ViewNode { Type = NodeType.Text, Text = Escape(text), Classes = { "tooltip-text" } });
            _tip = Build(node, "tooltip", null, null, null);
            _tip.Query<VisualElement>().ForEach(e => e.pickingMode = PickingMode.Ignore);
            _tip.style.position = Position.Absolute;
            _tipText = text;
            root.Add(_tip);
        }

        _tip.style.display = DisplayStyle.Flex;
        _tip.BringToFront();
        var bounds = over.worldBound;
        var size = _tip.layout;
        var width = float.IsNaN(size.width) ? 0 : size.width;
        var height = float.IsNaN(size.height) ? 0 : size.height;
        var area = root.layout;
        var top = bounds.yMax + 4;
        if (top + height > area.height && bounds.y - height - 4 >= 0)
        {
            top = bounds.y - height - 4;
        }

        _tip.style.left = Math.Max(0, Math.Min(bounds.x, area.width - width));
        _tip.style.top = Math.Max(0, Math.Min(top, area.height - height));
    }

    // When the field's caret fade starts: at the last change of its text, caret or selection, not at each rebuild (the
    // Activity tab rebuilds as its data changes, which would keep restarting the fade).
    private double CaretSince(string key)
    {
        if (key != _caretKey)
        {
            _caretKey = key;
            _caretSince = _context!.Now();
        }

        return _caretSince;
    }

    // A scroll view's wheel scrolling, eased toward its target instead of jumping a step per notch (the field's own
    // wheel gesture is left to the field).
    private void SmoothWheel(ScrollView view)
    {
        // A scrollbar that comes and goes changes the content's width, which re-wraps a text field, which rebuilds the
        // panel without it, which makes it come back: always shown, the width stays put.
        view.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
        view.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
        view.RegisterCallback<WheelEvent>(e =>
        {
            var onField = e.target is VisualElement target && _fieldViews.Any(v => v.TextBox.CanScroll && (v.Box == target || v.Box.Contains(target)));
            if (onField)
            {
                return; // the field's own handler decides (a gesture that began on the tab comes back here)
            }

            _context?.Controller.Wheel.Claim(overBox: false, _context.Now());
            WheelTab(view, e.delta.y);
            e.StopPropagation(); // instead of the scroll view's own jump
        }, TrickleDown.TrickleDown);
        _tabScroller = view;
        if (_tabTicker is null && _document != null)
        {
            _tabTicker = _document.rootVisualElement.schedule.Execute(TabTick).Every(16);
        }
    }

    // Eases the tab's scroll view toward its target. One ticker for the renderer's lifetime (rebuilds replace the scroll
    // view; a ticker per view piled up and played wheel input back later), idle until a rebuild's scroll position is
    // restored (it would start from the new view's top).
    private void TabTick()
    {
        if (_tabTarget is not { } target || _tabScroller?.panel is null || _restoring)
        {
            return;
        }

        var view = _tabScroller;
        var y = view.scrollOffset.y;
        var next = y + ((target - y) * 0.3f);
        if (Math.Abs(target - next) < 0.5f)
        {
            next = target;
            _tabTarget = null;
        }

        view.scrollOffset = new Vector2(view.scrollOffset.x, next);
    }

    // The tab's wheel step: its target moves, and SmoothWheel's ticker eases there.
    private void WheelTab(ScrollView view, float deltaY)
    {
        var max = Math.Max(0f, view.contentContainer.layout.height - view.contentViewport.layout.height);
        _tabTarget = Mathf.Clamp((_tabTarget ?? view.scrollOffset.y) + (deltaY * 18f), 0f, max);
    }

    // Scrolls a scroll view just enough to show an element, easing there (SmoothWheel's glide).
    private void GlideIntoView(ScrollView scroller, VisualElement target)
    {
        var view = scroller.contentViewport.worldBound;
        var r = target.worldBound;
        var delta = r.yMin < view.yMin ? r.yMin - view.yMin : r.yMax > view.yMax ? r.yMax - view.yMax : 0f;
        if (Math.Abs(delta) < 0.5f || float.IsNaN(delta))
        {
            return;
        }

        var max = Math.Max(0f, scroller.contentContainer.layout.height - scroller.contentViewport.layout.height);
        _tabTarget = Mathf.Clamp(scroller.scrollOffset.y + delta, 0f, max);
    }

    // The text field's measurement for the prompt's own wrapping, once the field has been laid out.
    private FieldMetrics? FieldMetrics()
    {
        var measure = _measure;
        return _fieldWidth > 0 && measure is not null
            ? new FieldMetrics(_fieldWidth, s => measure.MeasureTextSize(s, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x, slides: true)
            : null;
    }

    // A tab's periodic refresh updates the panel only when it shows that tab and a value its view shows changed
    // (ViewDependencies), and then in place where it can (Refresh): a rebuild replaces the elements under the pointer.
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
        _refresh = true; // in place where it can be (Refresh), a rebuild otherwise
    }

    private void OnModelChanged(OverlayModel model) => _dirty = true;

    // The text boxes count by their edits (Version: not the glide, which slides without rebuilds) and the keyboard focus.
    private string Signature(OverlayController c) => string.Join("|",
        Screen.width, Screen.height, c.Model.State, c.Model.Edge, c.Model.Offset.ToString("0.###", CultureInfo.InvariantCulture), c.Model.Docked, c.Model.Tab,
        c.EStop.Engaged, string.Join(",", c.Toasts.Visible.Select(t => t.Id + "x" + t.Count)), string.Join(",", c.Prompts.Pending.Select(p => p.Field is { } box ? p.Id + ":" + box.Version : p.Id)),
        c.Keyboard.Version, string.Join(",", _fieldViews.Select(v => v.TextBox.Version.ToString(CultureInfo.InvariantCulture)).ToArray())); // the arrow's tint (client activity) changes in place: TintArrow

    private void Rebuild(OverlayController controller, double width, double height)
    {
        Rebuilds++;
        SaveScroll(_layer!);
        _layer!.Clear();
        _sources.Clear();
        _elements.Clear();
        _updates.Clear();
        _tipTexts.Clear();
        _builtData = _liveData = null;
        _shellNode = null;
        _carets.Clear();
        _tooltip.Clear(); // its node is gone
        _fieldViews.Clear();
        var model = controller.Model;
        if (model.State == OverlayVisibility.Hidden)
        {
            return;
        }

        var arrowSize = Number(_context!.Theme.Token("$size.arrow", "renderer"), 48);
        var arrow = EdgeDock.Arrow(model.Edge, model.Offset, arrowSize, width, height);
        var arrowElement = Arrow(controller, arrow);
        _arrowElement = arrowElement;
        _arrowTint = null;
        _layer.Add(arrowElement);
        _elements["arrow"] = arrowElement;
        _sources.Add(new ElementSource("arrow", "arrow", ArrowNode, "arrow", null));
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
            _sources.Add(new ElementSource("header", "header", _shellHeader!, "shell/header", _builtData));
            _sources.Add(new ElementSource("panel", "panel/" + controller.Model.Tab, _shellBody!, "shell/content/" + (_shellBody!.Id ?? "0"), _builtData));
        }

        var cards = Cards(controller);
        if (cards is not null)
        {
            _sources.Add(new ElementSource("cards", "cards", _cardsNode!, "cards", null));
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
        PromptCard.Fill(body, controller.Prompts, Escape, FieldMetrics()); // the Activity tab's Questions
        var content = new ViewNode { Type = NodeType.Stack, Id = "content" };
        content.Style["flex-grow"] = "1";
        content.Style["overflow"] = "hidden";
        content.Style["margin-top"] = "$space.3";
        content.Children.Add(body);
        _shellHeader = header;
        _shellBody = body;
        var panel = new ViewNode { Type = NodeType.Panel, Id = "panel" };
        panel.Children.Add(header);
        panel.Children.Add(content);
        var data = controller.Views.Data(controller.Model.Tab);
        var tabState = new Dictionary<string, NodeState>(StringComparer.Ordinal) { ["tab-" + controller.Model.Tab] = NodeState.Checked };
        _groups = SizeGroups.Measure(panel, data, _context!.Theme, GroupTextWidth);
        _builtData = _liveData = data;
        _shellNode = panel;
        return Build(panel, "shell", data, null, tabState);
    }

    private VisualElement? Cards(OverlayController controller)
    {
        // While the panel is open, prompts are answered in its Activity tab, not in cards on top of it.
        var prompts = controller.Model.State == OverlayVisibility.Expanded ? Array.Empty<Prompt>() : controller.Prompts.Pending;
        if (prompts.Count == 0 && controller.Toasts.Visible.Count == 0)
        {
            return null;
        }

        var stack = new ViewNode { Type = NodeType.Stack, Id = "cards" };
        foreach (var prompt in prompts)
        {
            stack.Children.Add(PromptCard.Build(prompt, controller.Prompts, Escape, FieldMetrics()));
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

        _cardsNode = stack;
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
            case NodeType.Text when node.Box is not null:
                element = CaretText(text ?? string.Empty, node.Caret, node);
                break;
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
                element = Sparkline(value, _context!.Theme.Resolve(node, state).Color);
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
                // overflow: scroll is a vertical scroll view (its children go into its content container)
                if (node.Style.TryGetValue("overflow", out var overflow) && overflow == "scroll")
                {
                    var scroller = new ScrollView(ScrollViewMode.Vertical);
                    SmoothWheel(scroller);
                    element = scroller;
                }
                else
                {
                    element = new VisualElement();
                }

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

        if (node.Type == NodeType.Sparkline)
        {
            // Its bars stand side by side from its bottom (the theme's layout, applied above, made it a column).
            element.style.flexDirection = FlexDirection.Row;
            element.style.alignItems = Align.FlexEnd;
        }

        if (node.Command is not null && node.Type is not (NodeType.Button or NodeType.Toggle or NodeType.Slider or NodeType.Dropdown or NodeType.TextField))
        {
            element.RegisterCallback<ClickEvent>(_ => Run(node, data, item));
        }

        if (node.Tooltip is not null && Bindings.Text(node.Tooltip, data, item) is { Length: > 0 } tip)
        {
            // UI Toolkit's own tooltips only show in the editor: the overlay draws its own (see Tooltip).
            _tipTexts[element] = tip;
            element.RegisterCallback<PointerEnterEvent>(_ => _tooltip.Enter(element, _tipTexts.TryGetValue(element, out var now) ? now : tip, _context!.Now()));
            element.RegisterCallback<PointerLeaveEvent>(_ => _tooltip.Leave(element));
            element.RegisterCallback<PointerDownEvent>(_ => _tooltip.Clear(), TrickleDown.TrickleDown);
        }
        else if (text is { Length: > 0 } whole && _context!.Theme.Resolve(node, state).Ellipsis)
        {
            // A text cut off with an ellipsis: its whole text is its tooltip (only while it doesn't fit).
            element.RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (Truncated(element, whole))
                {
                    _tooltip.Enter(element, whole, _context!.Now());
                }
            });
            element.RegisterCallback<PointerLeaveEvent>(_ => _tooltip.Leave(element));
            element.RegisterCallback<PointerDownEvent>(_ => _tooltip.Clear(), TrickleDown.TrickleDown);
        }

        // A look of its own on hover: controls, and rows of lists (item is set) whose theme gives them one (row:hover). A
        // layout row outside a list (a heading with its buttons) stays as it is, like the headings around it.
        if (node.Command is not null || node.Type is NodeType.Toggle or NodeType.TextField or NodeType.Dropdown or NodeType.Slider || (item is not null && HasHover(node, state)))
        {
            States(element, node, state);
        }

        _elements[path] = element; // for clients that drive the overlay (OverlayAutomation)
        var live = item is null && data is not null && ReferenceEquals(data, _builtData); // built from the tab's data: refreshed in place
        if (live)
        {
            Updates(element, node, state);
        }

        if (node.Type is not (NodeType.List or NodeType.Tree or NodeType.Table or NodeType.Tabs))
        {
            var index = 0;
            foreach (var child in node.Children)
            {
                var childPath = path + "/" + (child.Id ?? index.ToString(CultureInfo.InvariantCulture));
                index++;
                var shown = Bindings.Visible(child.Visible, data, item);
                if (live && child.Visible is { } condition)
                {
                    _updates.Add(d => Bindings.Visible(condition, d, null) == shown); // shown or hidden: a rebuild
                }

                if (shown)
                {
                    element.Add(Build(child, childPath, data, item, states));
                }
            }
        }

        return element;
    }

    // How an element built from the tab's data follows that data in place (see Refresh).
    private void Updates(VisualElement element, ViewNode node, NodeState state)
    {
        if (node.Text is { } template && template.IndexOf('{') >= 0 && node.Box is null)
        {
            _updates.Add(d =>
            {
                var text = Bindings.Text(template, d, null);
                switch (element)
                {
                    case Button button when button.Q<Label>() is { } label:
                        label.text = text; // an icon button's text is in its label
                        break;
                    case UnityEngine.UIElements.TextElement textElement:
                        textElement.text = text;
                        break;
                    case Toggle toggle:
                        toggle.label = text;
                        break;
                }

                return true;
            });
        }

        if (node.Tooltip is { } tooltip)
        {
            _updates.Add(d =>
            {
                if (Bindings.Text(tooltip, d, null) is { Length: > 0 } tip)
                {
                    _tipTexts[element] = tip;
                }

                return true;
            });
        }

        if (node.Bind is not { } bind)
        {
            return;
        }

        switch (element)
        {
            case Toggle toggle:
                _updates.Add(d =>
                {
                    toggle.SetValueWithoutNotify(Bindings.Truthy(Bindings.Value(bind, d, null)));
                    return true;
                });
                break;
            case Slider slider:
                _updates.Add(d =>
                {
                    if (Bindings.Value(bind, d, null) is JsonNumber n)
                    {
                        slider.SetValueWithoutNotify((float)n.GetDouble());
                    }

                    return true;
                });
                break;
            case DropdownField dropdown:
                _updates.Add(d =>
                {
                    var choices = node.Items is not null && Bindings.Value(node.Items, d, null) is JsonArray array ? array.Select(Bindings.Plain).ToList() : new List<string>();
                    if (!choices.SequenceEqual(dropdown.choices))
                    {
                        return false; // other choices: a rebuild
                    }

                    dropdown.SetValueWithoutNotify(Bindings.Plain(Bindings.Value(bind, d, null)));
                    return true;
                });
                break;
            case TextField field:
                _updates.Add(d =>
                {
                    if (field.focusController?.focusedElement != field)
                    {
                        field.SetValueWithoutNotify(Bindings.Plain(Bindings.Value(bind, d, null))); // not while it's being edited
                    }

                    return true;
                });
                break;
            default:
                if (node.Type == NodeType.Progress && element.childCount > 0)
                {
                    var fill = element[0];
                    _updates.Add(d =>
                    {
                        var fraction = Bindings.Value(bind, d, null) is JsonNumber p ? Math.Max(0, Math.Min(1, p.GetDouble())) : 0;
                        fill.style.width = new Length((float)(fraction * 100), LengthUnit.Percent);
                        return true;
                    });
                }
                else if (node.Type == NodeType.Sparkline)
                {
                    var color = _context!.Theme.Resolve(node, state).Color;
                    _updates.Add(d =>
                    {
                        // Its bars are redrawn inside it (the element itself stays).
                        var fresh = Sparkline(Bindings.Value(bind, d, null), color);
                        element.Clear();
                        foreach (var bar in fresh.Children().ToList())
                        {
                            element.Add(bar);
                        }

                        return true;
                    });
                }

                break;
        }
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
        var source = new RowSource { Rows = rows, Key = RowsKey(rows) };
        var list = new ListView(rows, (float)rowHeight, PlainItem, (row, i) =>
        {
            row.Clear();
            Plain(row);
            if (i >= source.Rows.Count)
            {
                return;
            }

            var (rowItem, depth, rowPath) = source.Rows[i];
            var content = Build(template, rowPath, Live(data), rowItem, null);
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

        if (item is null && data is not null && ReferenceEquals(data, _builtData))
        {
            // New rows go into the same list: it keeps its scroll position, and rebinds only the rows in view.
            _updates.Add(d =>
            {
                var fresh = new List<(JsonValue Item, int Depth, string Path)>();
                if (node.Items is not null && Bindings.Value(node.Items, d, null) is { } now)
                {
                    Flatten(fresh, Items(now), node, path, 0);
                }

                var key = RowsKey(fresh);
                if (key != source.Key)
                {
                    // The same list object, its contents swapped: a new one goes through the list's reset (back to the top).
                    source.Rows.Clear();
                    source.Rows.AddRange(fresh);
                    source.Key = key;
                    list.RefreshItems();
                }

                return true;
            });
        }

        return list;
    }

    // A list's rows, swapped in place when the tab's data changes (see Refresh).
    private sealed class RowSource
    {
        public List<(JsonValue Item, int Depth, string Path)> Rows { get; set; } = new();

        public string Key { get; set; } = string.Empty;
    }

    private static string RowsKey(List<(JsonValue Item, int Depth, string Path)> rows) =>
        string.Join("\u0001", rows.Select(r => r.Path + "=" + r.Item).ToArray());

    // A list row's own element, without Unity's default list styling (its light hover and selection colours made rows
    // unreadable over the overlay's dark theme): the row's content is styled by the overlay's theme, hover included.
    private static VisualElement PlainItem() => Plain(new VisualElement());

    private static VisualElement Plain(VisualElement row)
    {
        row.style.backgroundColor = UnityEngine.Color.clear; // inline: Unity's :hover and :checked rules can't override it
        if (row.parent is { } wrapper && wrapper.ClassListContains("unity-collection-view__item"))
        {
            wrapper.style.backgroundColor = UnityEngine.Color.clear;
        }

        return row;
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
        var list = new ListView(rows, (float)rowHeight, PlainItem, (row, i) =>
        {
            row.Clear();
            Plain(row);
            if (i >= rows.Count)
            {
                return;
            }

            var r = TableRow(node, node.Columns.Select(c => Bindings.Plain(Bindings.Value("@." + c.Bind, Live(data), rows[i]))).ToList(), "value");
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
        if (item is null && data is not null && ReferenceEquals(data, _builtData))
        {
            var key = string.Join("\u0001", rows.Select(r => r.ToString()).ToArray());
            _updates.Add(d =>
            {
                var fresh = node.Items is not null && Bindings.Value(node.Items, d, null) is { } now ? Items(now).ToList() : new List<JsonValue>();
                var freshKey = string.Join("\u0001", fresh.Select(r => r.ToString()).ToArray());
                if (freshKey != key)
                {
                    key = freshKey;
                    rows.Clear(); // the same list object (a new one resets the list's scroll)
                    rows.AddRange(fresh);
                    list.RefreshItems();
                }

                return true;
            });
        }

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
        if (item is null && data is not null && ReferenceEquals(data, _builtData))
        {
            _updates.Add(d => node.Children.Where(c => Bindings.Visible(c.Visible, d, null)).SequenceEqual(visible)); // other pages: a rebuild
        }

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

    // Bars between the lowest and the highest value shown (from zero, values that barely change, such as a frame rate,
    // all drew at full height: a solid box), in the node's colour.
    private static VisualElement Sparkline(JsonValue? value, uint color)
    {
        var line = new VisualElement();
        line.style.flexDirection = FlexDirection.Row;
        line.style.alignItems = Align.FlexEnd;
        var values = value is JsonArray array ? array.OfType<JsonNumber>().Select(n => n.GetDouble()).ToList() : new List<double>();
        var shown = values.Skip(Math.Max(0, values.Count - 60)).ToList();
        var low = shown.Count == 0 ? 0 : shown.Min();
        var high = shown.Count == 0 ? 1 : shown.Max();
        foreach (var v in shown)
        {
            var bar = new VisualElement { pickingMode = PickingMode.Ignore };
            bar.style.flexGrow = 1;
            bar.style.marginRight = 1;
            var fraction = high - low < 1e-9 ? 0.5 : 0.1 + (0.9 * (v - low) / (high - low));
            bar.style.height = new Length((float)(fraction * 100), LengthUnit.Percent);
            bar.style.backgroundColor = Rgba(color);
            line.Add(bar);
        }

        return line;
    }

    // Theme states follow the pointer and focus: the resolved style for the new state is applied inline.
    // Whether the theme gives the node a look of its own on hover (e.g. row:hover), so it follows the pointer.
    private bool HasHover(ViewNode node, NodeState state)
    {
        var plain = _context!.Theme.Resolve(node, state);
        var hovered = _context.Theme.Resolve(node, state | NodeState.Hover);
        return plain.BackgroundColor != hovered.BackgroundColor || plain.BorderColor != hovered.BorderColor || plain.Color != hovered.Color;
    }

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

    // ---- driving the overlay (OverlayAutomation) ------------------------------------------------------------------------

    // The arrow as an element: a click expands or collapses the panel.
    private static readonly ViewNode ArrowNode = new() { Type = NodeType.Button, Id = "arrow", Command = "overlay.toggle" };

    /// <inheritdoc />
    public IReadOnlyList<ElementSource> Sources => _sources;

    /// <inheritdoc />
    public ElementPlace Locate(string path, string? listPath, int rowIndex)
    {
        if (_settings == null)
        {
            return new ElementPlace(null, "hidden");
        }

        if (_elements.TryGetValue(path, out var element) && element.panel is not null && element.resolvedStyle.display != DisplayStyle.None
            && element.resolvedStyle.visibility != Visibility.Hidden && !float.IsNaN(element.worldBound.width))
        {
            var bounds = element.worldBound;
            var shown = bounds;
            for (var parent = element.parent; parent is not null; parent = parent.parent)
            {
                if (parent is ScrollView view)
                {
                    shown = Intersect(shown, view.contentViewport.worldBound); // scrolled out of its list or scroll view
                }
            }

            var scale = _settings.scale;
            var screen = new Rect(0, 0, Screen.width / scale, Screen.height / scale);
            var onScreen = Intersect(shown, screen);
            var visibility = shown.width < 0.5f || shown.height < 0.5f ? "clipped"
                : onScreen.width < 0.5f || onScreen.height < 0.5f ? "offscreen"
                : Math.Abs(onScreen.width - bounds.width) < 0.5f && Math.Abs(onScreen.height - bounds.height) < 0.5f ? "visible"
                : "partial";
            return new ElementPlace((bounds.x * scale, bounds.y * scale, bounds.width * scale, bounds.height * scale), visibility);
        }

        // A list row that isn't built (scrolled away: lists build only the rows in view).
        return listPath is not null && _elements.TryGetValue(listPath, out var list) && list.panel is not null
            ? new ElementPlace(null, "clipped")
            : new ElementPlace(null, "hidden");
    }

    /// <inheritdoc />
    public bool ScrollIntoView(string path, string? listPath, int rowIndex)
    {
        if (_elements.TryGetValue(path, out var element) && element.panel is not null)
        {
            for (var parent = element.parent; parent is not null; parent = parent.parent)
            {
                if (parent is ScrollView view)
                {
                    if (view == _tabScroller)
                    {
                        _tabTarget = null; // the tab's eased wheel scrolling would pull it back
                    }

                    view.ScrollTo(element);
                }
            }

            return true;
        }

        if (listPath is not null && _elements.TryGetValue(listPath, out var list) && list is ListView rows && list.panel is not null)
        {
            rows.ScrollToItem(rowIndex); // the row is built once it's in view
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
        _commands?.Run(command, args);
        _dirty = true;
    }

    // The outline around the element OverlayAutomation reveals: follows it every frame until its time is up.
    private void PlaceOutline(VisualElement root)
    {
        var target = _outlinePath is { } path && _context!.Now() < _outlineUntil && _elements.TryGetValue(path, out var element) && element.panel is not null ? element : null;
        if (target is null)
        {
            if (_outlinePath is not null && _context!.Now() >= _outlineUntil)
            {
                _outlinePath = null;
            }

            if (_outline is not null)
            {
                _outline.style.display = DisplayStyle.None;
            }

            return;
        }

        if (_outline is null)
        {
            _outline = new VisualElement { name = "ov-outline", pickingMode = PickingMode.Ignore };
            _outline.style.position = UnityEngine.UIElements.Position.Absolute;
            _outline.style.borderTopWidth = _outline.style.borderBottomWidth = _outline.style.borderLeftWidth = _outline.style.borderRightWidth = 2;
            root.Add(_outline);
        }

        var accent = Color("$color.accent");
        _outline.style.borderTopColor = _outline.style.borderBottomColor = _outline.style.borderLeftColor = _outline.style.borderRightColor = accent;
        var bounds = target.worldBound;
        _outline.style.display = DisplayStyle.Flex;
        _outline.style.left = bounds.x - 3;
        _outline.style.top = bounds.y - 3;
        _outline.style.width = bounds.width + 6;
        _outline.style.height = bounds.height + 6;
        _outline.BringToFront();
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        var x = Math.Max(a.xMin, b.xMin);
        var y = Math.Max(a.yMin, b.yMin);
        return new Rect(x, y, Math.Max(0, Math.Min(a.xMax, b.xMax) - x), Math.Max(0, Math.Min(a.yMax, b.yMax) - y));
    }

    // Whether an element's text is wider than the room it has (an ellipsis cuts it off).
    private static bool Truncated(VisualElement element, string text)
    {
        var label = element as UnityEngine.UIElements.TextElement ?? element.Query<UnityEngine.UIElements.TextElement>().First();
        if (label is null || float.IsNaN(label.contentRect.width))
        {
            return false;
        }

        var width = label.MeasureTextSize(text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
        return width > label.contentRect.width + 0.5f;
    }

    // A text's width on one line in a node's font and size (for SizeGroups), measured by a hidden label with that font
    // and size (one per pair, kept on the panel's root). A label takes its font at the panel's next style pass: until it
    // has, the width is a guess, and the panel is rebuilt once it has.
    private double GroupTextWidth(ViewNode node, string text)
    {
        var style = _context!.Theme.Resolve(node);
        var key = style.Font + "|" + style.FontSize.ToString(CultureInfo.InvariantCulture);
        if (!_groupMeasures.TryGetValue(key, out var label) || label.panel is null)
        {
            label = new Label("M") { enableRichText = false, pickingMode = PickingMode.Ignore };
            Apply(label, node, NodeState.None); // the node's font and size
            label.style.position = Position.Absolute;
            label.style.visibility = Visibility.Hidden;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (_measuresReady.Add(label))
                {
                    _dirty = true; // styled now: measure again
                }
            });
            _document!.rootVisualElement.Add(label);
            _groupMeasures[key] = label;
        }

        return _measuresReady.Contains(label)
            ? label.MeasureTextSize(text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x
            : text.Length * style.FontSize * 0.6;
    }

    // Every look and layout property, inline (nothing left to Unity's defaults).
    private void Apply(VisualElement element, ViewNode node, NodeState state)
    {
        var resolved = _context!.Theme.Resolve(node, state);
        var layout = resolved.Layout;
        if (node.SizeGroup is { } group && _groups.TryGetValue(group, out var groupWidth))
        {
            layout.Width = Layout.Length.Px(groupWidth); // the group's width (SizeGroups)
        }

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
        s.overflow = layout.Overflow != Layout.Overflow.Visible || resolved.Ellipsis ? Overflow.Hidden : Overflow.Visible;
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

    // Runtime SDF font assets from the bundle's fonts, made once per font.
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

        data = Live(data); // refreshed in place since it was built

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
        // One still being put back keeps its saved position (for a moment: a position it can't reach any more is given up).
        if (!string.IsNullOrEmpty(key) && !(_unrestored.TryGetValue(key, out var since) && _context!.Now() - since < 0.5))
        {
            _scroll[key] = v.scrollOffset;
        }
    });

    // Puts a scroll view back where it was. A list measures its rows and clamps its own scroll position a few frames after
    // it's built (Unity's list virtualization: before that, any position past its first screen snaps back to the top), so
    // the position is set again every frame for a short while. Meanwhile SaveScroll keeps the saved one.
    private void Restore(ScrollView view, string key, Vector2 offset)
    {
        _unrestored[key] = _context!.Now();
        var frames = 0;
        view.scrollOffset = offset;
        view.schedule.Execute(() =>
        {
            if ((view.scrollOffset - offset).sqrMagnitude >= 0.25f)
            {
                view.scrollOffset = offset;
            }

            frames++;
        }).Every(0).Until(() =>
        {
            if (frames < 20 && view.panel is not null)
            {
                return false;
            }

            _unrestored.Remove(key);
            return true;
        });
    }

    private void RestoreScroll(VisualElement root)
    {
        _restoring = true;
        root.schedule.Execute(() =>
        {
            root.Query<ScrollView>().ForEach(v =>
            {
                var key = v.parent?.name ?? v.name;
                if (!string.IsNullOrEmpty(key) && _scroll.TryGetValue(key, out var offset))
                {
                    Restore(v, key, offset);
                }
            });
            _restoring = false;
        });
    }

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
