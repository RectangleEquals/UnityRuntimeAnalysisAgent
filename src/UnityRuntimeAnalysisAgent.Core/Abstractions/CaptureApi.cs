using System;
using System.Collections.Generic;

namespace UnityRuntimeAnalysisAgent.Core.Abstractions;

/// <summary>Screen and camera captures (main thread only).</summary>
public interface ICaptureApi
{
    /// <summary>Whether the game renders at all (a player started with <c>-nographics</c> doesn't).</summary>
    ModuleStatus Status { get; }

    /// <summary>The screen size in pixels.</summary>
    (int Width, int Height) ScreenSize { get; }

    /// <summary>The finished frame (<c>ScreenCapture.CaptureScreenshotAsTexture</c>); call at the end of a frame.</summary>
    ImagePixels CaptureScreen(int superSize);

    /// <summary>One camera rendered into a temporary render texture (<paramref name="camera"/> null → the main camera).</summary>
    ImagePixels CaptureCamera(object? camera, int width, int height);

    /// <summary>A GameObject's or component's screen rectangle in pixels, origin top left: a UI element's rect, or the
    /// projected bounds of its renderer or collider as seen by the main camera. Null if it has none on screen.</summary>
    (double X, double Y, double W, double H)? ScreenRectOf(object target);

    /// <summary>Encodes as JPEG (<c>ImageConversion</c>).</summary>
    byte[] EncodeJpg(ImagePixels image, int quality);
}

/// <summary>What the in-game overlay contributes to captures; absent while there is no overlay.</summary>
public interface ICaptureHooks
{
    /// <summary>Hides the overlay until the returned handle is disposed (for the captured frame).</summary>
    IDisposable HideOverlay();

    /// <summary>The overlay's current highlights: screen rectangles (origin top left) with labels.</summary>
    IReadOnlyList<(double X, double Y, double W, double H, string Label)> Highlights { get; }
}
