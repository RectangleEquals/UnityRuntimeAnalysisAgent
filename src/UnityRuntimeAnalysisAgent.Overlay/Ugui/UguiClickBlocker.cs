using System;
using System.Collections.Generic;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Unity;

namespace UnityRuntimeAnalysisAgent.Overlay.Ugui;

/// <summary>
/// <c>Overlay.BlockUiClicks</c> for renderers that aren't uGUI: invisible uGUI raycast targets over the overlay's
/// rectangles, on a canvas sorted just below the overlay, so the game's own uGUI never receives clicks meant for the
/// overlay. Agent-owned, so UI enumeration, marks and picks skip it.
/// </summary>
public sealed class UguiClickBlocker : IDisposable
{
    /// <summary>The blocker canvas's sort order (one below the UI Toolkit overlay panel's).</summary>
    public const short SortingOrder = short.MaxValue - 1;

    private readonly UguiOverlayBinder _binder = new();
    private readonly List<RectTransform> _blocks = new();
    private GameObject? _root;

    /// <summary>Whether its objects still exist.</summary>
    public bool Alive => _root != null;

    /// <summary>Creates the canvas. False when the game has no uGUI (nothing to block then).</summary>
    public bool Start()
    {
        if (!_binder.EnsureBound())
        {
            return false;
        }

        _root = new GameObject("UnityRuntimeAnalysisAgent overlay (click blocker)");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        _root.AddComponent<AgentOwned>();
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = SortingOrder;
        _binder.Add(_root, _binder.GraphicRaycaster);
        return true;
    }

    /// <summary>Covers these rectangles (screen pixels from the top-left) and nothing else.</summary>
    public void Cover(IReadOnlyList<Rect> rects)
    {
        if (_root == null)
        {
            return;
        }

        while (_blocks.Count < rects.Count)
        {
            var go = new GameObject("block", typeof(RectTransform));
            go.transform.SetParent(_root.transform, false);
            var image = _binder.Add(go, _binder.Image);
            _binder.Set(image, "color", new Color(0, 0, 0, 0));
            _binder.Set(image, "raycastTarget", true);
            var t = (RectTransform)go.transform;
            t.anchorMin = t.anchorMax = t.pivot = new Vector2(0, 1);
            _blocks.Add(t);
        }

        for (var i = 0; i < _blocks.Count; i++)
        {
            var active = i < rects.Count;
            if (_blocks[i].gameObject.activeSelf != active)
            {
                _blocks[i].gameObject.SetActive(active);
            }

            if (active)
            {
                _blocks[i].anchoredPosition = new Vector2(rects[i].x, -rects[i].y);
                _blocks[i].sizeDelta = new Vector2(rects[i].width, rects[i].height);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_root != null)
        {
            UnityEngine.Object.Destroy(_root);
        }

        _root = null;
        _blocks.Clear();
    }
}
