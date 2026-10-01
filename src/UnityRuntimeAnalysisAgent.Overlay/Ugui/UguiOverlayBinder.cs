using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Unity;

namespace UnityRuntimeAnalysisAgent.Overlay.Ugui;

/// <summary>
/// uGUI (<c>UnityEngine.UI</c>, a package a game can leave out) for the overlay, by reflection: the components the styled
/// uGUI renderer creates and the members it sets, resolved once. Missing pieces make the renderer unavailable (the overlay
/// falls back), never an error in the game.
/// </summary>
public sealed class UguiOverlayBinder : ModuleBinder
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Creates the binder.</summary>
    public UguiOverlayBinder()
        : base("ugui-overlay")
    {
    }

    /// <summary>The bound types.</summary>
    public Type Image { get; private set; } = null!;

    /// <inheritdoc cref="Image"/>
    public Type RawImage { get; private set; } = null!;

    /// <inheritdoc cref="Image"/>
    public Type Text { get; private set; } = null!;

    /// <inheritdoc cref="Image"/>
    public Type InputField { get; private set; } = null!;

    /// <inheritdoc cref="Image"/>
    public Type RectMask2D { get; private set; } = null!;

    /// <inheritdoc cref="Image"/>
    public Type CanvasScaler { get; private set; } = null!;

    /// <inheritdoc cref="Image"/>
    public Type GraphicRaycaster { get; private set; } = null!;

    /// <inheritdoc cref="Image"/>
    public Type EventTrigger { get; private set; } = null!;

    /// <inheritdoc cref="Image"/>
    public Type EventSystem { get; private set; } = null!;

    /// <inheritdoc cref="Image"/>
    public Type StandaloneInputModule { get; private set; } = null!;

    /// <summary>The Input System's UI module, when the game has the Input System.</summary>
    public Type? InputSystemUIInputModule { get; private set; }

    private Type _entry = null!;
    private Type _triggerType = null!;
    private Type _action = null!;
    private Type _baseEventData = null!;
    private PropertyInfo _pointerPosition = null!;
    private PropertyInfo _scrollDelta = null!;
    private PropertyInfo _current = null!;

    /// <inheritdoc />
    protected override void Bind()
    {
        Image = RequireType("UnityEngine.UI.Image");
        RawImage = RequireType("UnityEngine.UI.RawImage");
        Text = RequireType("UnityEngine.UI.Text");
        InputField = RequireType("UnityEngine.UI.InputField");
        RectMask2D = RequireType("UnityEngine.UI.RectMask2D");
        CanvasScaler = RequireType("UnityEngine.UI.CanvasScaler");
        GraphicRaycaster = RequireType("UnityEngine.UI.GraphicRaycaster");
        EventTrigger = RequireType("UnityEngine.EventSystems.EventTrigger");
        EventSystem = RequireType("UnityEngine.EventSystems.EventSystem");
        StandaloneInputModule = RequireType("UnityEngine.EventSystems.StandaloneInputModule");
        _entry = RequireType("UnityEngine.EventSystems.EventTrigger+Entry");
        _triggerType = RequireType("UnityEngine.EventSystems.EventTriggerType");
        _baseEventData = RequireType("UnityEngine.EventSystems.BaseEventData");
        var pointerEventData = RequireType("UnityEngine.EventSystems.PointerEventData");
        _pointerPosition = pointerEventData.GetProperty("position", Instance) ?? throw new ModuleUnavailableException("PointerEventData.position is missing.");
        _scrollDelta = pointerEventData.GetProperty("scrollDelta", Instance) ?? throw new ModuleUnavailableException("PointerEventData.scrollDelta is missing.");
        _current = EventSystem.GetProperty("current", BindingFlags.Public | BindingFlags.Static) ?? throw new ModuleUnavailableException("EventSystem.current is missing.");
        _action = typeof(UnityEngine.Events.UnityAction<>).MakeGenericType(_baseEventData);
        InputSystemUIInputModule = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
    }

    /// <summary>The EventSystem in charge, if any.</summary>
    public UnityEngine.Object? CurrentEventSystem => _current.GetValue(null, null) as UnityEngine.Object;

    /// <summary>Adds a component by type.</summary>
    public Component Add(GameObject go, Type type) => go.GetComponent(type) ?? go.AddComponent(type);

    /// <summary>Sets a property (or field) by name; enums can be given as their name.</summary>
    public void Set(object target, string member, object? value)
    {
        var type = target.GetType();
        if (type.GetProperty(member, Instance) is { CanWrite: true } property)
        {
            property.SetValue(target, Convert(value, property.PropertyType), null);
            return;
        }

        if (type.GetField(member, Instance) is { } field)
        {
            field.SetValue(target, Convert(value, field.FieldType));
            return;
        }

        throw new MissingMemberException(type.FullName, member);
    }

    /// <summary>Reads a property (or field, e.g. <c>EventTrigger.Entry.callback</c>) by name.</summary>
    public object? Get(object target, string member)
    {
        var type = target.GetType();
        return type.GetProperty(member, Instance) is { } property ? property.GetValue(target, null) : type.GetField(member, Instance)?.GetValue(target);
    }

    /// <summary>Adds an EventTrigger entry (PointerEnter, PointerExit, PointerDown, PointerUp, PointerClick, Scroll, BeginDrag, Drag, EndDrag).</summary>
    public void On(GameObject go, string eventType, Action<EventData> handler)
    {
        var trigger = Add(go, EventTrigger);
        var triggers = (IList)(Get(trigger, "triggers") ?? throw new MissingMemberException("EventTrigger.triggers"));
        var entry = Activator.CreateInstance(_entry);
        Set(entry, "eventID", Enum.Parse(_triggerType, eventType));
        var callback = Get(entry, "callback") ?? throw new MissingMemberException("EventTrigger.Entry.callback");
        var relay = new Relay(this, handler);
        var action = Delegate.CreateDelegate(_action, relay, typeof(Relay).GetMethod(nameof(Relay.Invoke))!);
        callback.GetType().GetMethod("AddListener", Instance)!.Invoke(callback, new object[] { action });
        triggers.Add(entry);
    }

    private static object? Convert(object? value, Type to) =>
        value is string name && to.IsEnum ? Enum.Parse(to, name) : value;

    /// <summary>What a pointer event says (screen position, origin bottom left; scroll delta).</summary>
    public readonly struct EventData
    {
        /// <summary>Creates the data.</summary>
        public EventData(Vector2 position, Vector2 scroll)
        {
            Position = position;
            Scroll = scroll;
        }

        /// <summary>The pointer position (screen pixels, origin bottom left).</summary>
        public Vector2 Position { get; }

        /// <summary>The scroll delta.</summary>
        public Vector2 Scroll { get; }
    }

    // Bound as a UnityAction<BaseEventData>: the parameter type is a base of BaseEventData, which delegate binding allows.
    private sealed class Relay
    {
        private readonly UguiOverlayBinder _binder;
        private readonly Action<EventData> _handler;

        public Relay(UguiOverlayBinder binder, Action<EventData> handler)
        {
            _binder = binder;
            _handler = handler;
        }

        public void Invoke(object data)
        {
            var position = data is not null && _binder._pointerPosition.DeclaringType!.IsInstanceOfType(data) ? (Vector2)_binder._pointerPosition.GetValue(data, null)! : Vector2.zero;
            var scroll = data is not null && _binder._scrollDelta.DeclaringType!.IsInstanceOfType(data) ? (Vector2)_binder._scrollDelta.GetValue(data, null)! : Vector2.zero;
            _handler(new EventData(position, scroll));
        }
    }
}
