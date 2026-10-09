using System;
using System.Collections.Generic;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Overlay.Views;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>
/// The emergency view, drawn with IMGUI when neither UI Toolkit nor uGUI can run: one small box in the corner of the
/// arrow's edge with the agent's status, why the full overlay isn't available, and E-STOP. Hidden state hides it.
/// </summary>
public sealed class ImguiEmergencyRenderer : IOverlayRenderer
{
    private GameObject? _host;
    private Drawer? _drawer;

    /// <inheritdoc />
    public string Name => "imgui";

    /// <summary>Why the full overlay isn't drawn (shown in the box).</summary>
    public string Reason { get; set; } = "";

    /// <inheritdoc />
    public bool Alive => _host != null && _drawer != null;

    /// <inheritdoc />
    public string? Failure => null;

    /// <inheritdoc />
    public IReadOnlyList<Rect> Occupied => _drawer?.Box is { } box ? new[] { box } : Array.Empty<Rect>();

    /// <inheritdoc />
    public IReadOnlyList<(Rect Rect, TextBox Box)> TextFields => Array.Empty<(Rect, TextBox)>(); // the emergency view has no text boxes

    /// <inheritdoc />
    public IReadOnlyList<ElementSource> Sources => Array.Empty<ElementSource>(); // nothing to drive: it only has E-STOP

    /// <inheritdoc />
    public ElementPlace Locate(string path, string? listPath, int rowIndex) => new(null, "hidden");

    /// <inheritdoc />
    public bool ScrollIntoView(string path, string? listPath, int rowIndex) => false;

    /// <inheritdoc />
    public void Outline(string path, double seconds)
    {
    }

    /// <inheritdoc />
    public void Run(string command, UnityLudometry.Protocol.Json.JsonObject args)
    {
    }

    /// <inheritdoc />
    public bool HandlesWheel => false;

    /// <inheritdoc />
    public bool TryStart(OverlayContext context, out string? reason)
    {
        _host = new GameObject("UnityRuntimeAnalysisAgent overlay (emergency)") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(_host);
        _drawer = _host.AddComponent<Drawer>();
        _drawer.Context = context;
        _drawer.Owner = this;
        reason = null;
        return true;
    }

    /// <inheritdoc />
    public void Update()
    {
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_host != null)
        {
            UnityEngine.Object.Destroy(_host);
        }

        _host = null;
        _drawer = null;
    }

    /// <summary>Draws the box (IMGUI needs a component's OnGUI).</summary>
    public sealed class Drawer : MonoBehaviour
    {
        private GUIStyle? _text;

        internal OverlayContext? Context { get; set; }

        internal ImguiEmergencyRenderer? Owner { get; set; }

        internal Rect? Box { get; private set; }

        private void OnGUI()
        {
            var context = Context;
            if (context is null || context.Controller.Model.State == OverlayVisibility.Hidden)
            {
                Box = null;
                return;
            }

            _text ??= new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 12 };
            var width = 320f;
            var height = context.Controller.EStop.Engaged ? 118f : 96f;
            var edge = context.Controller.Model.Edge;
            var x = edge == OverlayEdge.Left ? 8 : Screen.width - width - 8;
            var y = edge == OverlayEdge.Top ? 8 : Screen.height - height - 8;
            Box = new Rect(x, y, width, height);
            GUI.Box(Box.Value, GUIContent.none);
            GUILayout.BeginArea(new Rect(x + 8, y + 6, width - 16, height - 12));
            GUILayout.Label("UnityRuntimeAnalysisAgent — basic overlay", _text);
            GUILayout.Label(Owner?.Reason ?? "", _text);
            if (context.Controller.EStop.Engaged)
            {
                GUILayout.Label("E-STOP engaged: ReadOnly, automation stopped.", _text);
            }
            else if (GUILayout.Button("E-STOP"))
            {
                context.Controller.EngageEStop();
            }

            GUILayout.EndArea();
        }
    }
}
