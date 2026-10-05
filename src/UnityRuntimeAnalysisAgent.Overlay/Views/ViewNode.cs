using System;
using System.Collections.Generic;
using System.Linq;
using UnityLudometry.Protocol.Json;

namespace UnityRuntimeAnalysisAgent.Overlay.Views;

/// <summary>The node types a view may use; renderers implement each one.</summary>
public enum NodeType
{
    /// <summary>A box with a background (the default container).</summary>
    Panel,

    /// <summary>A box without chrome, for layout.</summary>
    Stack,

    /// <summary>Text (literal, with <c>{path}</c> bindings).</summary>
    Text,

    /// <summary>An icon from the icon atlas.</summary>
    Icon,

    /// <summary>An image file from the overlay's images folder.</summary>
    Image,

    /// <summary>An animated effect (a preset from the theme).</summary>
    Effect,

    /// <summary>A button that runs a command.</summary>
    Button,

    /// <summary>An on/off switch bound to a value.</summary>
    Toggle,

    /// <summary>A number in a range.</summary>
    Slider,

    /// <summary>One choice from a list.</summary>
    Dropdown,

    /// <summary>Editable text.</summary>
    TextField,

    /// <summary>A virtualised list of items rendered from a template.</summary>
    List,

    /// <summary>A lazy tree (children loaded on expand).</summary>
    Tree,

    /// <summary>Rows and columns.</summary>
    Table,

    /// <summary>Tab headers switching between children.</summary>
    Tabs,

    /// <summary>A progress bar.</summary>
    Progress,

    /// <summary>A small line chart of recent values.</summary>
    Sparkline,

    /// <summary>A small count or status pill.</summary>
    Badge,

    /// <summary>A thin rule.</summary>
    Separator,
}

/// <summary>A table column.</summary>
public sealed class ViewColumn
{
    /// <summary>The header text.</summary>
    public string Header { get; set; } = "";

    /// <summary>The value's path in each row.</summary>
    public string Bind { get; set; } = "";

    /// <summary>The width (a layout length, e.g. <c>120</c> or <c>30%</c>), or null to share the rest.</summary>
    public string? Width { get; set; }
}

/// <summary>
/// One node of a view: its type, style classes and inline style, bindings to view-model paths, visibility condition,
/// command and children. Renderer-independent; the renderer turns it into UI Toolkit elements or uGUI objects.
/// </summary>
public sealed class ViewNode
{
    /// <summary>The type.</summary>
    public NodeType Type { get; set; }

    /// <summary>An id, unique in its view (for commands, focus and tests).</summary>
    public string? Id { get; set; }

    /// <summary>Style classes from the theme, in order (later ones win).</summary>
    public List<string> Classes { get; } = new();

    /// <summary>Inline style (USS names), applied after the classes; values may be theme tokens (<c>$color.accent</c>).</summary>
    public Dictionary<string, string> Style { get; } = new(StringComparer.Ordinal);

    /// <summary>Literal text with <c>{path}</c> bindings (Text, Button, Badge, Toggle labels).</summary>
    public string? Text { get; set; }

    /// <summary>The value path (Toggle, Slider, Dropdown, TextField, Progress, Sparkline).</summary>
    public string? Bind { get; set; }

    /// <summary>When it's shown: a path (truthy), <c>!path</c>, <c>path == value</c> or <c>path != value</c>.</summary>
    public string? Visible { get; set; }

    /// <summary>The command a Button (or a list row, or a Toggle change) runs.</summary>
    public string? Command { get; set; }

    /// <summary>The command's arguments (values may be <c>{path}</c> bindings, resolved per row in lists).</summary>
    public JsonObject? Args { get; set; }

    /// <summary>The icon name (Icon, Button, Badge).</summary>
    public string? Icon { get; set; }

    /// <summary>The image file (Image).</summary>
    public string? Image { get; set; }

    /// <summary>The effect preset (Effect).</summary>
    public string? Effect { get; set; }

    /// <summary>A tooltip (text with bindings).</summary>
    public string? Tooltip { get; set; }

    /// <summary>The items' path (List, Tree, Table, Dropdown choices).</summary>
    public string? Items { get; set; }

    /// <summary>
    /// List/Tree rows size to their content (wrapped text shows in full) instead of all taking the first row's height.
    /// Every row is measured, so use it for short lists (notifications, history), not for thousands of rows.
    /// </summary>
    public bool WrapRows { get; set; }

    /// <summary>A Tree item's children path.</summary>
    public string? ChildrenPath { get; set; }

    /// <summary>The per-item template (List, Tree); paths inside it are relative to the item.</summary>
    public ViewNode? Template { get; set; }

    /// <summary>The columns (Table).</summary>
    public List<ViewColumn> Columns { get; } = new();

    /// <summary>A Slider's range.</summary>
    public (double Min, double Max)? Range { get; set; }

    /// <summary>The children, in order.</summary>
    public List<ViewNode> Children { get; } = new();

    /// <summary>This node and all its descendants (templates included), depth first.</summary>
    public IEnumerable<ViewNode> Descendants()
    {
        yield return this;
        foreach (var child in Children.Concat(Template is null ? Enumerable.Empty<ViewNode>() : new[] { Template }))
        {
            foreach (var node in child.Descendants())
            {
                yield return node;
            }
        }
    }
}

/// <summary>A view: one tab, panel or card, loaded from <c>overlay/views/&lt;name&gt;.json</c>.</summary>
public sealed class ViewDocument
{
    /// <summary>Creates the document.</summary>
    public ViewDocument(string name, ViewNode root, IReadOnlyList<string> warnings)
    {
        Name = name;
        Root = root;
        Warnings = warnings;
    }

    /// <summary>Its name (the tab id for tab views).</summary>
    public string Name { get; }

    /// <summary>The root node.</summary>
    public ViewNode Root { get; }

    /// <summary>What was skipped while loading, with where.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>A node by id.</summary>
    public ViewNode? Find(string id) => Root.Descendants().FirstOrDefault(n => n.Id == id);
}
