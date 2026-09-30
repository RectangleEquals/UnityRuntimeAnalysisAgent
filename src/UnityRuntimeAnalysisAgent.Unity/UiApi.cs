using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Abstractions;

namespace UnityRuntimeAnalysisAgent.Unity;

/// <summary>Marks the agent's own UI: nothing under an object carrying it is listed or driven by <c>ui.*</c>.</summary>
public sealed class AgentOwned : MonoBehaviour
{
}

/// <summary>
/// uGUI (<c>UnityEngine.UI</c>, which isn't an engine module) and TextMeshPro, reached by reflection so one agent build
/// works whether or not a game ships them. Actions go through the game's own handlers: pointer events through the
/// EventSystem, and value changes through the components' setters (their change events fire).
/// Screen rectangles are in pixels with the origin at the top left, like screenshots.
/// </summary>
internal sealed class UiApi : IUiApi
{
    private const int MaxImages = 8;
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

    private static readonly Type? ImguiType = Type.GetType("UnityEngine.GUI, UnityEngine.IMGUIModule") ?? Type.GetType("UnityEngine.GUI, UnityEngine");
    private static readonly Dictionary<Type, bool> OnGuiCache = new();
    private static readonly Dictionary<int, Camera?> CameraByLayer = new();
    private static Camera[]? _cameras;
    private static int _camerasFrame = -1;

    private readonly UguiBinder _ugui = new();
    private readonly UiToolkitProbe _uiToolkit = new();
    private readonly TmpBinder _tmp = new();

    public ModuleStatus UguiStatus => Status(_ugui);

    public ModuleStatus TmpStatus => Status(_tmp);

    public IReadOnlyList<UiElementFacts> Snapshot(bool onlyInteractable, bool onlyVisible, bool includeText, int limit)
    {
        var ui = Ugui();
        var tmp = _tmp.EnsureBound() ? _tmp : null;
        var items = new List<UiElementFacts>();
        // A screen opened this frame hasn't been laid out yet (Unity does that just before rendering): lay out now, as
        // the next frame will be drawn.
        Canvas.ForceUpdateCanvases();
        var system = ui.CurrentEventSystem();
        var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>()
            .Where(c => c.isRootCanvas && !IsAgentOwned(c.gameObject))
            .OrderByDescending(c => c.sortingOrder).ThenBy(c => c.name, StringComparer.Ordinal);
        foreach (var canvas in canvases)
        {
            foreach (var rect in canvas.GetComponentsInChildren<RectTransform>(includeInactive: !onlyVisible))
            {
                if (items.Count >= limit)
                {
                    return items;
                }

                var go = rect.gameObject;
                if ((onlyVisible && !go.activeInHierarchy) || IsAgentOwned(go))
                {
                    continue;
                }

                var element = Describe(ui, tmp, go, canvas, includeText, onlyVisible, system);
                if (element is null || (onlyVisible && !element.Visible) || (onlyInteractable && !element.Interactable) || (!includeText && element.Kind == "text"))
                {
                    continue;
                }

                items.Add(element);
            }
        }

        return items;
    }

    public UiElementFacts? Describe(object target)
    {
        var ui = Ugui();
        var go = GameObjectOf(target);
        var canvas = go.GetComponentInParent<Canvas>();
        Canvas.ForceUpdateCanvases();
        return canvas == null || !(go.transform is RectTransform) ? null
            : Describe(ui, _tmp.EnsureBound() ? _tmp : null, go, canvas.rootCanvas, includeText: true, onlyVisible: false, ui.CurrentEventSystem());
    }

    public UiActionOutcome Hover(object target, bool leave)
    {
        var ui = Ugui();
        var go = GameObjectOf(target);
        var system = ui.CurrentEventSystem();
        if (system is null)
        {
            return new UiActionOutcome(false, "none");
        }

        var pointer = Activator.CreateInstance(ui.PointerEventData, system)!;
        ui.PointerPosition.SetValue(pointer, Center(go), null);
        ui.PointerEventData.GetProperty("pointerEnter", Instance)?.SetValue(pointer, leave ? null : go, null);
        var handled = leave
            ? ui.ExecuteHierarchy(go, pointer, "pointerExitHandler", ui.PointerExit)
            : ui.ExecuteHierarchy(go, pointer, "pointerEnterHandler", ui.PointerEnter);
        return new UiActionOutcome(handled is not null, "eventSystem");
    }

    public UiScrollOutcome ScrollTo(object target)
    {
        var ui = Ugui();
        var rect = GameObjectOf(target).transform as RectTransform ?? throw new ArgumentException("The target isn't a UI element.");
        object? innermost = null;
        var scrolled = false;
        for (var t = rect.parent; t != null; t = t.parent)
        {
            if (t.GetComponent(ui.ScrollRect) is not { } scroll)
            {
                continue;
            }

            innermost ??= t.gameObject;
            var content = ui.ScrollRect.GetProperty("content", Instance)!.GetValue(scroll, null) as RectTransform;
            var viewport = (ui.ScrollRect.GetProperty("viewport", Instance)?.GetValue(scroll, null) as RectTransform) ?? (RectTransform)t;
            if (content == null || content.parent == null)
            {
                continue;
            }

            // How far the element sticks out of the viewport, in the viewport's space; the content moves back by that much.
            var inner = Bounds(viewport, rect);
            var window = viewport.rect;
            var horizontal = (bool)ui.ScrollRect.GetProperty("horizontal", Instance)!.GetValue(scroll, null);
            var vertical = (bool)ui.ScrollRect.GetProperty("vertical", Instance)!.GetValue(scroll, null);
            var delta = new Vector2(
                !horizontal ? 0 : inner.xMin < window.xMin ? inner.xMin - window.xMin : inner.xMax > window.xMax ? Math.Min(inner.xMax - window.xMax, inner.xMin - window.xMin) : 0,
                !vertical ? 0 : inner.yMax > window.yMax ? inner.yMax - window.yMax : inner.yMin < window.yMin ? Math.Max(inner.yMin - window.yMin, inner.yMax - window.yMax) : 0);
            if (delta.sqrMagnitude < 0.01f)
            {
                continue;
            }

            ui.ScrollRect.GetMethod("StopMovement", Instance)?.Invoke(scroll, null);
            content.localPosition -= content.parent.InverseTransformVector(viewport.TransformVector(delta));
            Canvas.ForceUpdateCanvases();
            scrolled = true;
        }

        return new UiScrollOutcome(scrolled, innermost);
    }

    public UiNavigateOutcome Navigate(string direction)
    {
        var ui = Ugui();
        var system = ui.CurrentEventSystem();
        var before = Selected(ui, system);
        if (system is null || before is null)
        {
            return new UiNavigateOutcome(false, before);
        }

        var move = direction switch
        {
            "left" => 0,
            "up" => 1,
            "right" => 2,
            "down" => 3,
            _ => throw new ArgumentException("direction must be up, down, left or right."),
        };
        var data = Activator.CreateInstance(ui.AxisEventData, system)!;
        ui.AxisEventData.GetProperty("moveDir", Instance)!.SetValue(data, Enum.ToObject(ui.MoveDirection, move), null);
        ui.Execute(before, data, "moveHandler", ui.MoveHandler);
        var after = Selected(ui, system);
        return new UiNavigateOutcome(after != before, after);
    }

    public UiFrameworksFacts Frameworks()
    {
        var facts = new UiFrameworksFacts { Ugui = UguiStatus, Tmp = TmpStatus };
        if (_ugui.EnsureBound())
        {
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>().Where(c => c.isRootCanvas && c.isActiveAndEnabled && !IsAgentOwned(c.gameObject)))
            {
                var (overlay, camera, world) = facts.Canvases;
                facts.Canvases = canvas.renderMode switch
                {
                    RenderMode.ScreenSpaceOverlay => (overlay + 1, camera, world),
                    RenderMode.ScreenSpaceCamera => (overlay, camera + 1, world),
                    _ => (overlay, camera, world + 1),
                };
            }

            if (_ugui.CurrentEventSystem() is { } system)
            {
                facts.EventSystemPresent = true;
                facts.InputModule = (_ugui.EventSystem.GetProperty("currentInputModule", Instance)?.GetValue(system, null) as Component)?.GetType().FullName;
            }
        }

        _uiToolkit.Read(facts, IsAgentOwned);
        facts.ImguiAvailable = ImguiType is not null;
        facts.ImguiBehaviours = ImguiType is null ? 0 : UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
            .Count(b => b.isActiveAndEnabled && HasOnGui(b.GetType()) && !IsAgentOwned(b.gameObject) && !IsAgentAssembly(b.GetType().Assembly));
        InputReport(facts);
        return facts;
    }

    public bool IsAgentOwned(object gameObjectOrComponent)
    {
        for (var t = GameObjectOf(gameObjectOrComponent).transform; t != null; t = t.parent)
        {
            if (t.GetComponent<AgentOwned>() != null || (t.gameObject.hideFlags & HideFlags.HideAndDontSave) == HideFlags.HideAndDontSave)
            {
                return true;
            }
        }

        return false;
    }

    public UiActionOutcome Click(object target)
    {
        var ui = Ugui();
        var go = GameObjectOf(target);
        var system = ui.CurrentEventSystem();
        if (system is not null)
        {
            var pointer = Activator.CreateInstance(ui.PointerEventData, system)!;
            ui.PointerPosition.SetValue(pointer, Center(go), null);
            ui.ExecuteHierarchy(go, pointer, "pointerEnterHandler", ui.PointerEnter);
            var pressed = ui.ExecuteHierarchy(go, pointer, "pointerDownHandler", ui.PointerDown);
            ui.ExecuteHierarchy(pressed ?? go, pointer, "pointerUpHandler", ui.PointerUp);
            var clicked = ui.ExecuteHierarchy(pressed ?? go, pointer, "pointerClickHandler", ui.PointerClick);
            return new UiActionOutcome(clicked is not null, "eventSystem");
        }

        // No EventSystem: the closest thing to a click the component offers.
        if (go.GetComponent(ui.Button) is { } button)
        {
            Invoke(ui.Button.GetProperty("onClick", Instance)!.GetValue(button, null), "Invoke");
            return new UiActionOutcome(true, "onClick");
        }

        if (go.GetComponent(ui.Toggle) is { } toggle)
        {
            var isOn = ui.Toggle.GetProperty("isOn", Instance)!;
            isOn.SetValue(toggle, !(bool)isOn.GetValue(toggle, null), null);
            return new UiActionOutcome(true, "setter");
        }

        return new UiActionOutcome(false, "none");
    }

    public UiActionOutcome SetText(object target, string text, bool submit)
    {
        var ui = Ugui();
        var go = GameObjectOf(target);
        var field = go.GetComponent(ui.InputField) ?? (_tmp.EnsureBound() ? go.GetComponent(_tmp.InputField) : null)
            ?? throw new ArgumentException($"{go.name} has no input field.");
        var type = field.GetType();
        type.GetProperty("text", Instance)!.SetValue(field, text, null);
        if (submit)
        {
            foreach (var name in new[] { "onEndEdit", "onSubmit" })
            {
                if ((type.GetProperty(name, Instance)?.GetValue(field, null) ?? type.GetField(name, Instance)?.GetValue(field)) is { } unityEvent)
                {
                    Invoke(unityEvent, "Invoke", text);
                }
            }
        }

        return new UiActionOutcome(true, submit ? "setter+submit" : "setter");
    }

    public UiActionOutcome SetValue(object target, object? value)
    {
        var ui = Ugui();
        var go = GameObjectOf(target);
        if (go.GetComponent(ui.Toggle) is { } toggle)
        {
            ui.Toggle.GetProperty("isOn", Instance)!.SetValue(toggle, value as bool? ?? throw new ArgumentException("A toggle's value is true or false."), null);
            return new UiActionOutcome(true, "setter");
        }

        foreach (var type in new[] { ui.Slider, ui.Scrollbar })
        {
            if (go.GetComponent(type) is { } component)
            {
                type.GetProperty("value", Instance)!.SetValue(component, (float)Number(value, "A slider's value is a number."), null);
                return new UiActionOutcome(true, "setter");
            }
        }

        var dropdown = go.GetComponent(ui.Dropdown) ?? (_tmp.EnsureBound() ? go.GetComponent(_tmp.Dropdown) : null);
        if (dropdown is not null)
        {
            dropdown.GetType().GetProperty("value", Instance)!.SetValue(dropdown, (int)Number(value, "A dropdown's value is an option index."), null);
            return new UiActionOutcome(true, "setter");
        }

        if (value is string or null && (go.GetComponent(ui.InputField) ?? (_tmp.EnsureBound() ? go.GetComponent(_tmp.InputField) : null)) is not null)
        {
            return SetText(go, value as string ?? string.Empty, submit: false);
        }

        throw new ArgumentException($"{go.name} has no toggle, slider, scrollbar, dropdown or input field.");
    }

    public UiActionOutcome Submit() => SendToSelected("submitHandler", Ugui().SubmitHandler);

    public UiActionOutcome Cancel() => SendToSelected("cancelHandler", Ugui().CancelHandler);

    public UiActionOutcome Select(object target)
    {
        var ui = Ugui();
        var system = ui.CurrentEventSystem();
        if (system is null)
        {
            return new UiActionOutcome(false, "none");
        }

        ui.EventSystem.GetMethod("SetSelectedGameObject", Instance, null, new[] { typeof(GameObject) }, null)!.Invoke(system, new object[] { GameObjectOf(target) });
        return new UiActionOutcome(true, "eventSystem");
    }

    private UiActionOutcome SendToSelected(string handler, Type handlerType)
    {
        var ui = Ugui();
        var system = ui.CurrentEventSystem();
        if (system is null || ui.EventSystem.GetProperty("currentSelectedGameObject", Instance)!.GetValue(system, null) is not GameObject selected || selected == null)
        {
            return new UiActionOutcome(false, "none");
        }

        var data = Activator.CreateInstance(ui.BaseEventData, system)!;
        var handled = (bool)ui.Execute(selected, data, handler, handlerType)!;
        return new UiActionOutcome(handled, "eventSystem");
    }

    private UiElementFacts? Describe(UguiBinder ui, TmpBinder? tmp, GameObject go, Canvas canvas, bool includeText, bool onlyVisible, object? system)
    {
        var selectable = go.GetComponent(ui.Selectable);
        var scroll = go.GetComponent(ui.ScrollRect);
        var text = go.GetComponent(ui.Text) ?? (tmp is null ? null : go.GetComponent(tmp.Text));
        var image = go.GetComponent(ui.Image) ?? go.GetComponent(ui.RawImage);
        var (clicks, hovers) = Handlers(ui, go);
        if (selectable is null && scroll is null && text is null && image is null && !clicks && !hovers)
        {
            return null;
        }

        if (onlyVisible && selectable is null && scroll is null && !clicks && !hovers && (text ?? image) is Behaviour { enabled: false })
        {
            return null;
        }

        var element = new UiElementFacts(go)
        {
            Kind = selectable is not null ? KindOf(ui, tmp, go) : scroll is not null ? "scrollRect" : text is not null ? "text" : image is not null ? "image" : "other",
            Canvas = canvas.name,
            SortingOrder = canvas.sortingOrder,
            ScreenRect = ScreenRect((RectTransform)go.transform, canvas),
            DrawnToTexture = DrawnToTexture(canvas) is not null,
        };
        (element.Visibility, element.VisibleRect) = go.activeInHierarchy ? Visibility(ui, (RectTransform)go.transform, canvas, element.ScreenRect) : ("hidden", null);

        // How it can be used: a Selectable says so itself; other handlers work unless a CanvasGroup switches them off.
        if (selectable is not null)
        {
            element.Interactable = (bool)ui.Selectable.GetMethod("IsInteractable", Instance)!.Invoke(selectable, null);
            element.Interaction = element.Interactable ? "clickable" : "disabled";
            element.Navigation = Navigation(ui, selectable);
        }
        else if (clicks || scroll is not null)
        {
            element.Interactable = GroupsAllow(go);
            element.Interaction = element.Interactable ? "clickable" : "disabled";
        }
        else if (hovers)
        {
            element.Interaction = GroupsAllow(go) ? "hover" : "disabled";
        }

        element.ScrollContainer = ScrollContainer(ui, go);
        element.Selected = system is not null && Selected(ui, system) == go;

        if (includeText)
        {
            // Controls show a child's text (a button's label); a scroll view's children are its content, not its label.
            var shown = text ?? (selectable is not null || ((clicks || hovers) && scroll is null) ? go.GetComponentInChildren(ui.Text) ?? (tmp is null ? null : go.GetComponentInChildren(tmp.Text)) : null);
            var raw = shown is null ? null : shown.GetType().GetProperty("text", Instance)?.GetValue(shown, null) as string;
            element.Text = StripRichText(raw);
            element.RawText = raw is not null && RichTextTag.IsMatch(raw) ? raw : null;
        }

        foreach (var component in (element.Kind == "image" ? new[] { image! } : go.GetComponentsInChildren(ui.Image).Concat(go.GetComponentsInChildren(ui.RawImage))).Take(MaxImages))
        {
            if (component.GetType().GetProperty("sprite", Instance)?.GetValue(component, null) is Sprite sprite)
            {
                element.Images.Add((sprite.name, sprite.texture != null ? sprite.texture.name : null));
            }
            else if (component.GetType().GetProperty("texture", Instance)?.GetValue(component, null) is Texture texture && texture != null)
            {
                element.Images.Add((null, texture.name));
            }
        }

        ReadState(ui, tmp, go, element);
        if (includeText && element.Kind == "inputField" && element.Value is string { Length: > 0 } typed)
        {
            element.Text = typed; // what the field shows; its first text child is the placeholder
            element.RawText = null;
        }

        if (element.Interactable && system is not null && element.Visible && !element.DrawnToTexture)
        {
            element.RaycastBlocked = Blocked(ui, system, go);
        }

        return element;
    }

    // Which pointer handlers the GameObject's own components implement: click-like ones (click, down, up, submit, drag,
    // drop, scroll) and hover-only ones (enter, exit).
    private static (bool Clicks, bool Hovers) Handlers(UguiBinder ui, GameObject go)
    {
        bool clicks = false, hovers = false;
        foreach (var component in go.GetComponents<MonoBehaviour>())
        {
            if (component == null || !component.enabled)
            {
                continue;
            }

            var type = component.GetType();
            clicks |= ui.ClickHandlers.Any(h => h.IsAssignableFrom(type));
            hovers |= ui.HoverHandlers.Any(h => h.IsAssignableFrom(type));
        }

        return (clicks, hovers);
    }

    // CanvasGroups up the hierarchy (until one ignores its parents) must be interactable and block raycasts.
    private static bool GroupsAllow(GameObject go)
    {
        for (var t = go.transform; t != null; t = t.parent)
        {
            var group = t.GetComponent<CanvasGroup>();
            if (group == null || ((object)group is Behaviour { enabled: false }))
            {
                continue;
            }

            if (!group.interactable || !group.blocksRaycasts)
            {
                return false;
            }

            if (group.ignoreParentGroups)
            {
                break;
            }
        }

        return true;
    }

    private static object? ScrollContainer(UguiBinder ui, GameObject go)
    {
        for (var t = go.transform.parent; t != null; t = t.parent)
        {
            if (t.GetComponent(ui.ScrollRect) is not null)
            {
                return t.gameObject;
            }
        }

        return null;
    }

    private static UiNavigationFacts? Navigation(UguiBinder ui, Component selectable)
    {
        var navigation = ui.Selectable.GetProperty("navigation", Instance)?.GetValue(selectable, null);
        var mode = navigation?.GetType().GetProperty("mode", Instance)?.GetValue(navigation, null)?.ToString() ?? "None";
        GameObject? Find(string method) =>
            (ui.Selectable.GetMethod(method, Instance, null, Type.EmptyTypes, null)?.Invoke(selectable, null) as Component) is { } found && found != null ? found.gameObject : null;
        return new UiNavigationFacts
        {
            Mode = mode.Length == 0 ? "none" : char.ToLowerInvariant(mode[0]) + mode.Substring(1),
            Up = Find("FindSelectableOnUp"),
            Down = Find("FindSelectableOnDown"),
            Left = Find("FindSelectableOnLeft"),
            Right = Find("FindSelectableOnRight"),
        };
    }

    private static GameObject? Selected(UguiBinder ui, object? system) =>
        system is not null && ui.EventSystem.GetProperty("currentSelectedGameObject", Instance)!.GetValue(system, null) is GameObject selected && selected != null ? selected : null;

    // The element's rectangle in another RectTransform's local space.
    private static Rect Bounds(RectTransform space, RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        var local = corners.Select(c => (Vector2)space.InverseTransformPoint(c)).ToList();
        return Rect.MinMaxRect(local.Min(p => p.x), local.Min(p => p.y), local.Max(p => p.x), local.Max(p => p.y));
    }

    private static readonly System.Text.RegularExpressions.Regex RichTextTag = new("<[^<>]{1,256}>", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Rich-text markup (<b>, <color=…>, TextMeshPro's <sprite …>) isn't what a reader sees; null when nothing is left.
    private static string? StripRichText(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var plain = RichTextTag.Replace(text, string.Empty).Trim();
        return plain.Length == 0 ? null : plain;
    }

    // How much of an element can be seen: the screen and every enabled RectMask2D/Mask above it (scroll views clip
    // through those) cut it; a CanvasGroup at alpha 0 hides it. Returns the visibility and the visible part.
    private static (string Visibility, (double X, double Y, double W, double H)? Rect) Visibility(UguiBinder ui, RectTransform rect, Canvas canvas, (double X, double Y, double W, double H) full)
    {
        if (full.W < 1 || full.H < 1)
        {
            return ("hidden", null);
        }

        var (width, height) = SurfaceSize(canvas);
        double x0 = Math.Max(0, full.X), y0 = Math.Max(0, full.Y), x1 = Math.Min(width, full.X + full.W), y1 = Math.Min(height, full.Y + full.H);
        if (x1 - x0 < 1 || y1 - y0 < 1)
        {
            return ("offscreen", null);
        }

        var groupsDone = false;
        for (var t = rect.transform; t != null; t = t.parent)
        {
            // CanvasGroup became a Behaviour (with enabled) after 2018.1: a disabled one has no effect.
            var group = groupsDone ? null : t.GetComponent<CanvasGroup>();
            if (group != null && !((object)group is Behaviour { enabled: false }))
            {
                if (group.alpha <= 0.001f)
                {
                    return ("hidden", null);
                }

                groupsDone = group.ignoreParentGroups;
            }

            if (t != rect.transform && t is RectTransform clip && (Enabled(t.GetComponent(ui.RectMask2D)) || Enabled(t.GetComponent(ui.Mask))))
            {
                var r = ScreenRect(clip, canvas);
                x0 = Math.Max(x0, r.X);
                y0 = Math.Max(y0, r.Y);
                x1 = Math.Min(x1, r.X + r.W);
                y1 = Math.Min(y1, r.Y + r.H);
            }
        }

        if (x1 - x0 < 1 || y1 - y0 < 1)
        {
            return ("clipped", null);
        }

        var whole = x1 - x0 >= full.W - 1 && y1 - y0 >= full.H - 1;
        return (whole ? "visible" : "partial", (x0, y0, x1 - x0, y1 - y0));
    }

    private static bool Enabled(Component? component) => component is Behaviour behaviour && behaviour != null && behaviour.enabled;

    private static string KindOf(UguiBinder ui, TmpBinder? tmp, GameObject go) =>
        go.GetComponent(ui.Button) is not null ? "button"
        : go.GetComponent(ui.Toggle) is not null ? "toggle"
        : go.GetComponent(ui.Slider) is not null ? "slider"
        : go.GetComponent(ui.Dropdown) is not null || (tmp is not null && go.GetComponent(tmp.Dropdown) is not null) ? "dropdown"
        : go.GetComponent(ui.InputField) is not null || (tmp is not null && go.GetComponent(tmp.InputField) is not null) ? "inputField"
        : "other";

    private static void ReadState(UguiBinder ui, TmpBinder? tmp, GameObject go, UiElementFacts element)
    {
        if (go.GetComponent(ui.Toggle) is { } toggle)
        {
            element.IsOn = (bool)ui.Toggle.GetProperty("isOn", Instance)!.GetValue(toggle, null);
        }

        foreach (var type in new[] { ui.Slider, ui.Scrollbar })
        {
            if (go.GetComponent(type) is { } component)
            {
                element.Value = (double)(float)type.GetProperty("value", Instance)!.GetValue(component, null);
            }
        }

        var dropdown = go.GetComponent(ui.Dropdown) ?? (tmp is null ? null : go.GetComponent(tmp.Dropdown));
        if (dropdown is not null)
        {
            element.Value = (int)dropdown.GetType().GetProperty("value", Instance)!.GetValue(dropdown, null);
            if (dropdown.GetType().GetProperty("options", Instance)?.GetValue(dropdown, null) is IEnumerable options)
            {
                element.Options = options.Cast<object>().Select(o => o.GetType().GetProperty("text", Instance)?.GetValue(o, null) as string ?? string.Empty).ToList();
            }
        }

        var input = go.GetComponent(ui.InputField) ?? (tmp is null ? null : go.GetComponent(tmp.InputField));
        if (input is not null)
        {
            element.Value = input.GetType().GetProperty("text", Instance)?.GetValue(input, null) as string;
        }
    }

    // Whether the topmost UI raycast hit at the element's centre is something else (another element covers it).
    private static bool Blocked(UguiBinder ui, object system, GameObject go)
    {
        var pointer = Activator.CreateInstance(ui.PointerEventData, system)!;
        ui.PointerPosition.SetValue(pointer, Center(go), null);
        var results = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ui.RaycastResult))!;
        ui.EventSystem.GetMethod("RaycastAll", Instance)!.Invoke(system, new[] { pointer, results });
        if (results.Count == 0)
        {
            return false;
        }

        var hit = ui.RaycastResult.GetProperty("gameObject", Instance)?.GetValue(results[0], null) as GameObject
            ?? ui.RaycastResult.GetField("m_GameObject", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(results[0]) as GameObject;
        return hit != null && !hit.transform.IsChildOf(go.transform);
    }

    private static Vector2 Center(GameObject go)
    {
        var rect = go.transform as RectTransform;
        var canvas = go.GetComponentInParent<Canvas>();
        if (rect is null || canvas is null)
        {
            return Vector2.zero;
        }

        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        var centre = (corners[0] + corners[2]) / 2f;
        var camera = CameraOf(canvas.rootCanvas);
        return camera == null ? (Vector2)centre : RectTransformUtility.WorldToScreenPoint(camera, centre);
    }

    // The camera a canvas is drawn through: the one set on it; none for overlay canvases (and camera-space ones without a
    // camera, which Unity draws as overlays). A world-space canvas without one is drawn by every camera that sees its
    // layer: the main camera when it does, else the deepest enabled one, else a disabled one drawing into a texture (games
    // render those by hand). Worked out once per frame.
    private static Camera? CameraOf(Canvas canvas)
    {
        canvas = canvas.rootCanvas;
        if (canvas.worldCamera != null || canvas.renderMode != RenderMode.WorldSpace)
        {
            return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
        }

        if (_camerasFrame != Time.frameCount)
        {
            _camerasFrame = Time.frameCount;
            _cameras = null;
            CameraByLayer.Clear();
        }

        var layer = canvas.gameObject.layer;
        if (!CameraByLayer.TryGetValue(layer, out var camera))
        {
            var main = Camera.main;
            _cameras ??= UnityEngine.Object.FindObjectsOfType<Camera>().OrderByDescending(c => c.enabled).ThenByDescending(c => c.depth).ToArray();
            camera = main != null && Sees(main, layer) ? main
                : _cameras.FirstOrDefault(c => c.enabled && Sees(c, layer)) ?? _cameras.FirstOrDefault(c => c.targetTexture != null && Sees(c, layer)) ?? main;
            CameraByLayer[layer] = camera;
        }

        return camera;
    }

    private static bool Sees(Camera camera, int layer) => (camera.cullingMask & (1 << layer)) != 0;


    internal static (double X, double Y, double W, double H) ScreenRect(RectTransform rect, Canvas canvas)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        var camera = CameraOf(canvas);
        if (camera != null && corners.Any(c => camera.WorldToScreenPoint(c).z <= 0))
        {
            return (-1_000_000, -1_000_000, 1, 1); // behind the camera: nowhere on screen
        }

        var points = corners.Select(c => camera == null ? (Vector2)c : RectTransformUtility.WorldToScreenPoint(camera, c)).ToList();
        double minX = points.Min(p => p.x), maxX = points.Max(p => p.x), minY = points.Min(p => p.y), maxY = points.Max(p => p.y);
        return (minX, SurfaceSize(canvas).Height - maxY, maxX - minX, maxY - minY);
    }

    // What a canvas is drawn onto: the screen, or the render texture its camera draws into (UI shown on an in-world
    // screen or a post-processed surface). Rectangles and visibility are measured in that surface's pixels.
    private static (int Width, int Height) SurfaceSize(Canvas canvas) =>
        DrawnToTexture(canvas) is { } texture ? (texture.width, texture.height) : (Screen.width, Screen.height);

    private static RenderTexture? DrawnToTexture(Canvas canvas) => CameraOf(canvas.rootCanvas) is { } camera && camera.targetTexture != null ? camera.targetTexture : null;

    private static double Number(object? value, string message) => value switch
    {
        long l => l,
        int i => i,
        double d => d,
        float f => f,
        _ => throw new ArgumentException(message),
    };

    private static void Invoke(object? unityEvent, string method, params object[] args)
    {
        if (unityEvent is null)
        {
            return;
        }

        unityEvent.GetType().GetMethods(Instance).First(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(unityEvent, args);
    }

    private static GameObject GameObjectOf(object target) => target switch
    {
        GameObject go => go,
        Component component => component.gameObject,
        _ => throw new ArgumentException("The target must be a GameObject or a component."),
    };

    private static ModuleStatus Status(ModuleBinder binder)
    {
        binder.EnsureBound();
        return new ModuleStatus(binder.Available, binder.Version, binder.Reason);
    }

    private UguiBinder Ugui() => _ugui.EnsureBound() ? _ugui : throw new NotSupportedException(_ugui.Reason);

    /// <summary>The uGUI and EventSystems types the agent uses.</summary>
    private sealed class UguiBinder : ModuleBinder
    {
        private MethodInfo? _execute;
        private MethodInfo? _executeHierarchy;
        private PropertyInfo? _current;

        public UguiBinder()
            : base("ugui")
        {
        }

        public Type Selectable { get; private set; } = null!;
        public Type Button { get; private set; } = null!;
        public Type Toggle { get; private set; } = null!;
        public Type Slider { get; private set; } = null!;
        public Type Scrollbar { get; private set; } = null!;
        public Type Dropdown { get; private set; } = null!;
        public Type InputField { get; private set; } = null!;
        public Type ScrollRect { get; private set; } = null!;
        public Type Text { get; private set; } = null!;
        public Type Image { get; private set; } = null!;
        public Type RawImage { get; private set; } = null!;
        public Type RectMask2D { get; private set; } = null!;
        public Type Mask { get; private set; } = null!;
        public Type EventSystem { get; private set; } = null!;
        public Type PointerEventData { get; private set; } = null!;
        public Type BaseEventData { get; private set; } = null!;
        public Type RaycastResult { get; private set; } = null!;
        public PropertyInfo PointerPosition { get; private set; } = null!;
        public Type PointerEnter { get; private set; } = null!;
        public Type PointerExit { get; private set; } = null!;
        public Type MoveHandler { get; private set; } = null!;
        public Type AxisEventData { get; private set; } = null!;
        public Type MoveDirection { get; private set; } = null!;
        public Type[] ClickHandlers { get; private set; } = Array.Empty<Type>();
        public Type[] HoverHandlers { get; private set; } = Array.Empty<Type>();
        public Type PointerDown { get; private set; } = null!;
        public Type PointerUp { get; private set; } = null!;
        public Type PointerClick { get; private set; } = null!;
        public Type SubmitHandler { get; private set; } = null!;
        public Type CancelHandler { get; private set; } = null!;

        public object? CurrentEventSystem() => _current!.GetValue(null, null) is UnityEngine.Object system && system != null ? system : null;

        public object? Execute(GameObject target, object data, string handler, Type handlerType) =>
            _execute!.MakeGenericMethod(handlerType).Invoke(null, new[] { target, data, Functor(handler) });

        public GameObject? ExecuteHierarchy(GameObject target, object data, string handler, Type handlerType) =>
            _executeHierarchy!.MakeGenericMethod(handlerType).Invoke(null, new[] { target, data, Functor(handler) }) as GameObject;

        protected override void Bind()
        {
            TryLoad("UnityEngine.UI");
            const string ui = "UnityEngine.UI.", events = "UnityEngine.EventSystems.";
            Selectable = RequireType(ui + "Selectable");
            Button = RequireType(ui + "Button");
            Toggle = RequireType(ui + "Toggle");
            Slider = RequireType(ui + "Slider");
            Scrollbar = RequireType(ui + "Scrollbar");
            Dropdown = RequireType(ui + "Dropdown");
            InputField = RequireType(ui + "InputField");
            ScrollRect = RequireType(ui + "ScrollRect");
            Text = RequireType(ui + "Text");
            Image = RequireType(ui + "Image");
            RawImage = RequireType(ui + "RawImage");
            RectMask2D = RequireType(ui + "RectMask2D");
            Mask = RequireType(ui + "Mask");
            EventSystem = RequireType(events + "EventSystem");
            PointerEventData = RequireType(events + "PointerEventData");
            BaseEventData = RequireType(events + "BaseEventData");
            RaycastResult = RequireType(events + "RaycastResult");
            PointerEnter = RequireType(events + "IPointerEnterHandler");
            PointerExit = RequireType(events + "IPointerExitHandler");
            MoveHandler = RequireType(events + "IMoveHandler");
            AxisEventData = RequireType(events + "AxisEventData");
            MoveDirection = RequireType(events + "MoveDirection");
            HoverHandlers = new[] { PointerEnter, PointerExit };
            PointerDown = RequireType(events + "IPointerDownHandler");
            PointerUp = RequireType(events + "IPointerUpHandler");
            PointerClick = RequireType(events + "IPointerClickHandler");
            SubmitHandler = RequireType(events + "ISubmitHandler");
            ClickHandlers = new[] { "IPointerClickHandler", "IPointerDownHandler", "IPointerUpHandler", "ISubmitHandler", "IBeginDragHandler", "IDragHandler", "IDropHandler", "IScrollHandler" }
                .Select(n => RequireType(events + n)).ToArray();
            CancelHandler = RequireType(events + "ICancelHandler");
            PointerPosition = PointerEventData.GetProperty("position", Instance) ?? throw new ModuleUnavailableException("PointerEventData.position is missing.");
            _current = EventSystem.GetProperty("current", Static) ?? throw new ModuleUnavailableException("EventSystem.current is missing.");
            var executeEvents = RequireType(events + "ExecuteEvents");
            _execute = Generic(executeEvents, "Execute");
            _executeHierarchy = Generic(executeEvents, "ExecuteHierarchy");
            Version = Known(Selectable.Assembly.GetName().Version);
        }

        private static MethodInfo Generic(Type type, string name) =>
            type.GetMethods(Static).FirstOrDefault(m => m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == 3)
            ?? throw new ModuleUnavailableException($"ExecuteEvents.{name} is missing.");

        private object Functor(string handler) =>
            _execute!.DeclaringType!.GetProperty(handler, Static)?.GetValue(null, null) ?? throw new MissingMemberException("ExecuteEvents", handler);
    }

    /// <summary>The TextMeshPro types the agent uses.</summary>
    private sealed class TmpBinder : ModuleBinder
    {
        public TmpBinder()
            : base("tmp")
        {
        }

        public Type Text { get; private set; } = null!;
        public Type InputField { get; private set; } = null!;
        public Type Dropdown { get; private set; } = null!;

        protected override void Bind()
        {
            TryLoad("Unity.TextMeshPro");
            Text = RequireType("TMPro.TMP_Text");
            InputField = RequireType("TMPro.TMP_InputField");
            Dropdown = RequireType("TMPro.TMP_Dropdown");
            Version = Known(Text.Assembly.GetName().Version);
        }
    }

    private static bool HasOnGui(Type type)
    {
        lock (OnGuiCache)
        {
            if (!OnGuiCache.TryGetValue(type, out var has))
            {
                has = type.GetMethod("OnGUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) is not null;
                OnGuiCache[type] = has;
            }

            return has;
        }
    }

    private static bool IsAgentAssembly(Assembly assembly) => assembly.GetName().Name?.StartsWith("UnityRuntimeAnalysisAgent", StringComparison.Ordinal) ?? false;

    // UnityEngine.Input by reflection (D-001): it moved from CoreModule to InputLegacyModule in 2019.1.
    private static readonly Type? LegacyInput = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule")
        ?? Type.GetType("UnityEngine.Input, UnityEngine.CoreModule") ?? Type.GetType("UnityEngine.Input, UnityEngine");

    // Input: the Input Manager answers unless the project switched it off (then it throws); the Input System package
    // answers when it's loaded.
    private static void InputReport(UiFrameworksFacts facts)
    {
        var legacy = false;
        if (LegacyInput?.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static) is { } mousePosition)
        {
            try
            {
                mousePosition.GetValue(null, null);
                legacy = true;
            }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
            {
                // The project uses only the Input System package.
            }
        }

        var inputSystem = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.InputSystem");
        var gamepad = inputSystem?.GetType("UnityEngine.InputSystem.Gamepad", throwOnError: false);
        facts.InputSystemVersion = inputSystem?.GetName().Version?.ToString();
        facts.InputHandling = (legacy, inputSystem is not null) switch
        {
            (true, true) => "both",
            (false, true) => "inputSystem",
            (true, false) => "inputManager",
            _ => "unknown",
        };
        if (gamepad?.GetProperty("all", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null) is ICollection all)
        {
            facts.Gamepads = all.Count;
        }
        else if (legacy && LegacyInput!.GetMethod("GetJoystickNames", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) is string[] names)
        {
            facts.Gamepads = names.Count(n => !string.IsNullOrEmpty(n));
        }
    }

    /// <summary>Runtime UI Toolkit (UIDocument, Unity 2021.2+), by reflection: which documents are active, on which panels.</summary>
    private sealed class UiToolkitProbe : ModuleBinder
    {
        public UiToolkitProbe()
            : base("uiToolkit")
        {
        }

        private Type Document { get; set; } = null!;

        public void Read(UiFrameworksFacts facts, Func<object, bool> agentOwned)
        {
            // The module can be there without runtime UI (UIDocument arrived in 2021.2): available is the module, runtime
            // support is UIDocument.
            var runtime = EnsureBound();
            var module = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "UnityEngine.UIElementsModule");
            facts.UiToolkit = new ModuleStatus(module, module ? Application.unityVersion : null,
                !module ? "UnityEngine.UIElementsModule isn't loaded." : runtime ? null : "This Unity version has no runtime UI Toolkit (UIDocument).");
            facts.UiToolkitRuntime = runtime;
            if (!runtime)
            {
                return;
            }

            var panels = new Dictionary<string, (double Sort, int Documents)>(StringComparer.Ordinal);
            foreach (var document in UnityEngine.Object.FindObjectsOfType(Document).OfType<Behaviour>().Where(d => d.isActiveAndEnabled && !agentOwned(d.gameObject)))
            {
                facts.UiDocuments++;
                var settings = Document.GetProperty("panelSettings", Instance)?.GetValue(document, null) as UnityEngine.Object;
                var name = settings != null ? settings.name : "(none)";
                var sort = settings != null && settings.GetType().GetProperty("sortingOrder", Instance)?.GetValue(settings, null) is float order ? order : 0;
                panels[name] = (sort, panels.TryGetValue(name, out var seen) ? seen.Documents + 1 : 1);
            }

            foreach (var panel in panels.OrderBy(p => p.Value.Sort).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                facts.Panels.Add((panel.Key, panel.Value.Sort, panel.Value.Documents));
            }
        }

        protected override void Bind()
        {
            TryLoad("UnityEngine.UIElementsModule");
            Document = RequireType("UnityEngine.UIElements.UIDocument");
            Version = Application.unityVersion;
        }
    }

    // Package assemblies are often versioned 0.0.0.0 (which says nothing).
    private static string? Known(Version? version) => version is null || version == new Version(0, 0, 0, 0) ? null : version.ToString();

    // A game's UI assembly may not be loaded yet when the agent binds; it's in the game's Managed folder if the game uses it.
    private static void TryLoad(string assembly)
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == assembly))
        {
            return;
        }

        try
        {
            Assembly.Load(new AssemblyName(assembly));
        }
        catch (Exception)
        {
            // Not shipped by this game: the binder reports what's missing.
        }
    }
}
