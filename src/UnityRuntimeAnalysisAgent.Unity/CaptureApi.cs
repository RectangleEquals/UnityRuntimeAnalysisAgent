using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityRuntimeAnalysisAgent.Core.Abstractions;

namespace UnityRuntimeAnalysisAgent.Unity;

/// <summary>Screen and camera captures against the 2018.1 API.</summary>
internal sealed class CaptureApi : ICaptureApi
{
    public ModuleStatus Status => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
        ? new ModuleStatus(false, null, "The game runs without graphics (started with -nographics): there is nothing to capture.")
        : new ModuleStatus(true, SystemInfo.graphicsDeviceType.ToString(), null);

    public (int Width, int Height) ScreenSize => (Screen.width, Screen.height);

    public ImagePixels CaptureScreen(int superSize)
    {
        var texture = ScreenCapture.CaptureScreenshotAsTexture(superSize);
        try
        {
            return Pixels(texture);
        }
        finally
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    public ImagePixels CaptureCamera(object? camera, int width, int height)
    {
        var cam = CameraOf(camera) ?? throw new ArgumentException("There is no camera to capture (no main camera).");
        var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        var previousTarget = cam.targetTexture;
        var previousActive = RenderTexture.active;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        try
        {
            cam.targetTexture = target;
            cam.Render();
            RenderTexture.active = target;
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            texture.Apply(false);
            return Pixels(texture);
        }
        finally
        {
            cam.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(target);
            UnityEngine.Object.Destroy(texture);
        }
    }

    public (double X, double Y, double W, double H)? ScreenRectOf(object target)
    {
        var go = target switch
        {
            GameObject g => g,
            Component c => c.gameObject,
            _ => null,
        };
        if (go == null)
        {
            return null;
        }

        if (go.transform is RectTransform rect && go.GetComponentInParent<Canvas>() is { } canvas && canvas != null)
        {
            return UiApi.ScreenRect(rect, canvas.rootCanvas);
        }

        Bounds? bounds = go.GetComponent<Renderer>() is { } renderer && renderer != null ? renderer.bounds
            : go.GetComponent<Collider>() is { } collider && collider != null ? collider.bounds
            : go.GetComponent<Collider2D>() is { } collider2D && collider2D != null ? collider2D.bounds
            : null;
        var camera = Camera.main;
        if (bounds is not { } b || camera == null)
        {
            return null;
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var any = false;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
            var point = camera.WorldToScreenPoint(corner);
            if (point.z < 0)
            {
                continue; // behind the camera
            }

            any = true;
            minX = Math.Min(minX, point.x);
            maxX = Math.Max(maxX, point.x);
            minY = Math.Min(minY, point.y);
            maxY = Math.Max(maxY, point.y);
        }

        return any ? (minX, Screen.height - maxY, maxX - minX, maxY - minY) : null;
    }

    public byte[] EncodeJpg(ImagePixels image, int quality)
    {
        var texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false);
        try
        {
            texture.LoadRawTextureData(image.Rgba);
            texture.Apply(false);
            return ImageConversion.EncodeToJPG(texture, quality);
        }
        finally
        {
            UnityEngine.Object.Destroy(texture);
        }
    }

    private static Camera? CameraOf(object? camera) => camera switch
    {
        null => Camera.main,
        Camera c => c,
        Component c => c.GetComponent<Camera>(),
        GameObject g => g.GetComponent<Camera>(),
        _ => throw new ArgumentException("The camera target isn't a camera."),
    };

    // RGBA32 bytes, rows bottom to top (Unity's order).
    private static ImagePixels Pixels(Texture2D texture)
    {
        var colors = texture.GetPixels32();
        var rgba = new byte[colors.Length * 4];
        for (var i = 0; i < colors.Length; i++)
        {
            rgba[i * 4] = colors[i].r;
            rgba[(i * 4) + 1] = colors[i].g;
            rgba[(i * 4) + 2] = colors[i].b;
            rgba[(i * 4) + 3] = colors[i].a;
        }

        return new ImagePixels(texture.width, texture.height, rgba);
    }
}
