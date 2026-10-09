using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Overlay;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>A drawn part of the overlay, for <see cref="OverlayAutomation"/>: its view node, where the renderer built it, and its data.</summary>
/// <param name="Area"><c>panel</c>, <c>header</c>, <c>cards</c> or <c>arrow</c>.</param>
/// <param name="IdPrefix">The ids of its elements start with this (<c>panel/&lt;tab&gt;</c>, <c>header</c>, …).</param>
/// <param name="Node">The view node the renderer built.</param>
/// <param name="Path">The renderer's path of that node (children are <c>path/&lt;id or index&gt;</c>, list rows <c>path/0.&lt;index&gt;</c>).</param>
/// <param name="Data">The data its bindings read.</param>
public sealed record ElementSource(string Area, string IdPrefix, ViewNode Node, string Path, JsonValue? Data);

/// <summary>Where an element is drawn now (screen pixels from the top-left) and how much of it shows.</summary>
public readonly record struct ElementPlace((double X, double Y, double W, double H)? Rect, string Visibility);

/// <summary>What a renderer provides for driving the overlay: what it drew and where, scrolling, outlining, commands.</summary>
public interface IOverlayAutomationSurface
{
    /// <summary>The parts of the overlay drawn now.</summary>
    IReadOnlyList<ElementSource> Sources { get; }

    /// <summary>Where the element at a renderer path is drawn; a list row also names its list and index (it may not be built).</summary>
    ElementPlace Locate(string path, string? listPath, int rowIndex);

    /// <summary>Scrolls the element at a path (or a list's row) into view. False when there's nothing to scroll to.</summary>
    bool ScrollIntoView(string path, string? listPath, int rowIndex);

    /// <summary>Outlines the element at a path for a while.</summary>
    void Outline(string path, double seconds);

    /// <summary>Runs an overlay command as a click on one of its elements would.</summary>
    void Run(string command, JsonObject args);
}

/// <summary>
/// The overlay's elements for clients that drive it (<see cref="IOverlayAutomation"/>), the same for every renderer: the
/// drawn views are walked as the renderers build them (same paths), with ids from the view's ids and indices, list rows
/// named by their item's <c>key</c> or <c>id</c>. Where each is and how much shows comes from the renderer.
/// </summary>
public sealed class OverlayAutomation : IOverlayAutomation
{
    private readonly IOverlayAutomationSurface _surface;
    private readonly OverlayController _controller;

    /// <summary>Drives the overlay a renderer draws.</summary>
    public OverlayAutomation(IOverlayAutomationSurface surface, OverlayController controller)
    {
        _surface = surface;
        _controller = controller;
    }

    /// <inheritdoc />
    public IReadOnlyList<OverlayElementState> Elements() => Walk().Select(State).ToList();

    /// <inheritdoc />
    public bool ScrollIntoView(string id) => Walk().FirstOrDefault(e => e.Id == id) is { } e && _surface.ScrollIntoView(e.Path, e.ListPath, e.RowIndex);

    /// <inheritdoc />
    public void Outline(string id, double seconds)
    {
        if (Walk().FirstOrDefault(e => e.Id == id) is { } e)
        {
            _surface.Outline(e.Path, seconds);
        }
    }

    /// <inheritdoc />
    public string Invoke(string id, JsonValue? value)
    {
        var e = Walk().FirstOrDefault(w => w.Id == id) ?? throw new KeyNotFoundException(id);
        var node = e.Node;
        var args = node.Args is not null ? Bindings.ResolveArgs(node.Args, e.Data, e.Item) : new JsonObject();
        if (node.Args is null && e.Item is not null)
        {
            args.Set("item", e.Item);
        }

        switch (node.Type)
        {
            case NodeType.Toggle when node.Command is { } toggle:
                var on = value is JsonBoolean b ? b.Value : !Bindings.Truthy(node.Bind is null ? null : Bindings.Value(node.Bind, e.Data, e.Item));
                args.Set("value", on ? JsonBoolean.True : JsonBoolean.False);
                _surface.Run(toggle, args);
                return toggle;
            case NodeType.Slider when node.Command is { } slide:
                args.Set("value", value is JsonNumber n ? n : throw new InvalidOperationException($"'{id}' is a slider: pass its value as a number."));
                _surface.Run(slide, args);
                return slide;
            case NodeType.Dropdown when node.Command is { } choose:
                args.Set("value", value is JsonString s ? s : throw new InvalidOperationException($"'{id}' is a dropdown: pass the choice as text."));
                _surface.Run(choose, args);
                return choose;
        }

        if (node.Box is { } box)
        {
            _controller.Keyboard.Focus(box); // a click in a text box gives it the keyboard
            return "focus";
        }

        if (node.Command is { } command)
        {
            _surface.Run(command, args);
            return command;
        }

        if (e.RowCommand is { } row)
        {
            // A row of a clickable list: the list's command with the row's item.
            var rowArgs = row.Args is not null ? Bindings.ResolveArgs(row.Args, e.Data, e.Item) : new JsonObject { { "item", e.Item ?? JsonNull.Instance } };
            _surface.Run(row.Command!, rowArgs);
            return row.Command!;
        }

        throw new InvalidOperationException($"'{id}' does nothing when clicked.");
    }

    private OverlayElementState State(Walked e)
    {
        var node = e.Node;
        var place = _surface.Locate(e.Path, e.ListPath, e.RowIndex);
        var text = node.Text is null ? null : Bindings.Text(node.Text, e.Data, e.Item);
        JsonValue? value = node.Box is { } box ? new JsonString(box.Text) : node.Bind is null ? null : Bindings.Value(node.Bind, e.Data, e.Item);
        var command = node.Command ?? e.RowCommand?.Command;
        var interaction = node.Box is not null || node.Type is NodeType.Toggle or NodeType.Slider or NodeType.Dropdown or NodeType.TextField
            ? "editable"
            : command is not null ? "clickable"
            : node.Type is NodeType.List or NodeType.Tree or NodeType.Table || (node.Style.TryGetValue("overflow", out var overflow) && overflow == "scroll") ? "scrollable"
            : "display";
        return new OverlayElementState
        {
            Id = e.Id,
            Parent = e.Parent,
            Area = e.Area,
            Type = node.Box is not null ? "textBox" : Name(node.Type),
            Text = text,
            Value = value,
            Command = command,
            Tooltip = node.Tooltip is null ? null : Bindings.Text(node.Tooltip, e.Data, e.Item),
            Interaction = interaction,
            Enabled = true,
            Focused = node.Box is not null && ReferenceEquals(node.Box, _controller.Keyboard.Focused),
            Visibility = place.Visibility,
            Rect = place.Rect,
            Box = node.Box,
        };
    }

    // Every element of the drawn parts, in drawing order, as the renderers build them.
    private List<Walked> Walk()
    {
        var all = new List<Walked>();
        foreach (var source in _surface.Sources)
        {
            Walk(source.Node, source.Path, source.IdPrefix, null, source.Area, source.Data, null, null, -1, null, all);
        }

        return all;
    }

    private static void Walk(ViewNode node, string path, string id, string? parent, string area, JsonValue? data, JsonValue? item, string? listPath, int rowIndex, ViewNode? rowCommand, List<Walked> all)
    {
        if (!Bindings.Visible(node.Visible, data, item))
        {
            return;
        }

        all.Add(new Walked(id, path, parent, area, node, data, item, listPath, rowIndex, rowCommand));
        if (node.Type is NodeType.List or NodeType.Tree && node.Template is { } template && node.Items is { } itemsPath)
        {
            if (Bindings.Value(itemsPath, data, item) is JsonArray rows)
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var rowPath = path + "/0." + i.ToString(CultureInfo.InvariantCulture);
                    var rowId = id + "/" + (Key(row) ?? "0." + i.ToString(CultureInfo.InvariantCulture));
                    Walk(template, rowPath, rowId, id, area, data, row, path, i, node.Command is not null ? node : null, all);
                }
            }

            return;
        }

        if (node.Type is NodeType.Table or NodeType.Tabs)
        {
            return; // their rows and pages are the renderer's own
        }

        var index = 0;
        foreach (var child in node.Children)
        {
            var segment = child.Id ?? index.ToString(CultureInfo.InvariantCulture);
            index++;
            Walk(child, path + "/" + segment, id + "/" + segment, id, area, data, item, listPath, rowIndex, rowCommand, all);
        }
    }

    // A list item's name in ids: its key or id (stable as the list changes), when it has one.
    private static string? Key(JsonValue row)
    {
        if (row is not JsonObject o)
        {
            return null;
        }

        foreach (var name in new[] { "key", "id" })
        {
            switch (o[name])
            {
                case JsonString s when s.Value.Length > 0:
                    return s.Value.Replace("/", "_");
                case JsonNumber n:
                    return n.ToString();
            }
        }

        return null;
    }

    private static string Name(NodeType type)
    {
        var name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private sealed record Walked(string Id, string Path, string? Parent, string Area, ViewNode Node, JsonValue? Data, JsonValue? Item, string? ListPath, int RowIndex, ViewNode? RowCommand);
}
