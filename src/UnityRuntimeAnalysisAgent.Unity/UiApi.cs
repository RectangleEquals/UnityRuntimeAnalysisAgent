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

    private readonly UguiBinder _ugui = new();
    private readonly TmpBinder _tmp = new();

    public ModuleStatus UguiStatus => Status(_ugui);

    public ModuleStatus TmpStatus => Status(_tmp);

    public IReadOnlyList<UiElementFacts> Snapshot(bool onlyInteractable, bool onlyVisible, bool includeText, int limit)
    {
        var ui = Ugui();
        var tmp = _tmp.EnsureBound() ? _tmp : null;
        var items = new List<UiElementFacts>();
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

                var element = Describe(ui, tmp, go, canvas, includeText, onlyVisible);
                if (element is null || (onlyVisible && !element.Visible) || (onlyInteractable && !element.Interactable) || (!includeText && element.Kind == "text"))
                {
                    continue;
                }

                items.Add(element);
            }
        }

        return items;
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

    private UiElementFacts? Describe(UguiBinder ui, TmpBinder? tmp, GameObject go, Canvas canvas, bool includeText, bool onlyVisible)
    {
        var selectable = go.GetComponent(ui.Selectable);
        var scroll = go.GetComponent(ui.ScrollRect);
        var text = go.GetComponent(ui.Text) ?? (tmp is null ? null : go.GetComponent(tmp.Text));
        var image = go.GetComponent(ui.Image) ?? go.GetComponent(ui.RawImage);
        if (selectable is null && scroll is null && text is null && image is null)
        {
            return null;
        }

        if (onlyVisible && selectable is null && scroll is null && (text ?? image) is Behaviour { enabled: false })
        {
            return null;
        }

        var element = new UiElementFacts(go)
        {
            Kind = selectable is not null ? KindOf(ui, tmp, go) : scroll is not null ? "scrollRect" : text is not null ? "text" : "image",
            Interactable = selectable is not null && (bool)ui.Selectable.GetMethod("IsInteractable", Instance)!.Invoke(selectable, null),
            Canvas = canvas.name,
            SortingOrder = canvas.sortingOrder,
            ScreenRect = ScreenRect((RectTransform)go.transform, canvas),
        };
        var visible = VisibleRect(ui, (RectTransform)go.transform, canvas, element.ScreenRect);
        element.Visible = go.activeInHierarchy && visible is not null;
        element.VisibleRect = visible is { } v && !v.Equals(element.ScreenRect) ? v : null;

        if (includeText)
        {
            var shown = text ?? (selectable is not null || scroll is not null ? go.GetComponentInChildren(ui.Text) ?? (tmp is null ? null : go.GetComponentInChildren(tmp.Text)) : null);
            element.Text = StripRichText(shown is null ? null : shown.GetType().GetProperty("text", Instance)?.GetValue(shown, null) as string);
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
        }
        if (element.Interactable && ui.CurrentEventSystem() is { } system)
        {
            element.RaycastBlocked = Blocked(ui, system, go);
        }

        return element;
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

    // The part of an element that can be seen: inside the screen and every enabled RectMask2D/Mask above it (scroll views
    // clip through those), and not faded out by a CanvasGroup (alpha 0). Null when nothing of it can be seen.
    private static (double X, double Y, double W, double H)? VisibleRect(UguiBinder ui, RectTransform rect, Canvas canvas, (double X, double Y, double W, double H) full)
    {
        double x0 = Math.Max(0, full.X), y0 = Math.Max(0, full.Y), x1 = Math.Min(Screen.width, full.X + full.W), y1 = Math.Min(Screen.height, full.Y + full.H);
        var groupsDone = false;
        for (var t = rect.transform; t != null; t = t.parent)
        {
            // CanvasGroup became a Behaviour (with enabled) after 2018.1: a disabled one has no effect.
            var group = groupsDone ? null : t.GetComponent<CanvasGroup>();
            if (group != null && !((object)group is Behaviour { enabled: false }))
            {
                if (group.alpha <= 0.001f)
                {
                    return null;
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

        return x1 - x0 >= 1 && y1 - y0 >= 1 ? (x0, y0, x1 - x0, y1 - y0) : null;
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
        return canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? (Vector2)centre : RectTransformUtility.WorldToScreenPoint(canvas.rootCanvas.worldCamera, centre);
    }

    internal static (double X, double Y, double W, double H) ScreenRect(RectTransform rect, Canvas canvas)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        var points = corners.Select(c => canvas.renderMode == RenderMode.ScreenSpaceOverlay ? (Vector2)c : RectTransformUtility.WorldToScreenPoint(canvas.worldCamera, c)).ToList();
        double minX = points.Min(p => p.x), maxX = points.Max(p => p.x), minY = points.Min(p => p.y), maxY = points.Max(p => p.y);
        return (minX, Screen.height - maxY, maxX - minX, maxY - minY);
    }

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
            PointerDown = RequireType(events + "IPointerDownHandler");
            PointerUp = RequireType(events + "IPointerUpHandler");
            PointerClick = RequireType(events + "IPointerClickHandler");
            SubmitHandler = RequireType(events + "ISubmitHandler");
            CancelHandler = RequireType(events + "ICancelHandler");
            PointerPosition = PointerEventData.GetProperty("position", Instance) ?? throw new ModuleUnavailableException("PointerEventData.position is missing.");
            _current = EventSystem.GetProperty("current", Static) ?? throw new ModuleUnavailableException("EventSystem.current is missing.");
            var executeEvents = RequireType(events + "ExecuteEvents");
            _execute = Generic(executeEvents, "Execute");
            _executeHierarchy = Generic(executeEvents, "ExecuteHierarchy");
            Version = Selectable.Assembly.GetName().Version?.ToString();
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
            Version = Text.Assembly.GetName().Version?.ToString();
        }
    }

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
