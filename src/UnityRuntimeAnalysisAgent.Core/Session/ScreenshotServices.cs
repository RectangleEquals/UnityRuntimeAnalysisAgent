using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using UnityRuntimeAnalysisAgent.Core.Content;
using UnityRuntimeAnalysisAgent.Core.Data;
using UnityRuntimeAnalysisAgent.Core.Diagnostics;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>
/// Screenshots for LLM vision: the finished frame (or one camera) captured at the end of a frame,
/// then cropped, downscaled and annotated on a worker, encoded (PNG on the worker, JPEG through Unity on the main
/// thread) and written atomically to the caller's path. Numbered <c>ui-marks</c> map back to handles the client can click.
/// </summary>
internal sealed class ScreenshotServices
{
    public const int MaxMarks = 99;
    public const uint MarkColor = 0xFF2D95FF;
    public const uint HighlightColor = 0x2DB8FFFF;
    private const int WorkerTimeoutMs = 60_000;

    private readonly DataModel _data;
    private readonly MainThreadPump _pump;
    private readonly IUnityApi _unity;

    public ScreenshotServices(DataModel data, MainThreadPump pump, IUnityApi unity)
    {
        _data = data;
        _pump = pump;
        _unity = unity;
    }

    /// <summary>The in-game overlay's capture hooks; null while there is no overlay.</summary>
    public ICaptureHooks? Hooks { get; set; }

    private ICaptureApi Capture => _unity.Capture is { } capture && capture.Status.Available ? capture
        : throw DataErrors.Unsupported(_unity.Capture?.Status.Reason ?? "There is no game to capture.");

    [RpcMethod(Methods.ScreenshotCapture, DefaultTimeoutMs = 30_000, MaxTimeoutMs = 600_000)]
    public IEnumerable<object?> CaptureScreen(RequestContext context, CaptureOptions p)
    {
        var capture = Capture;
        var jpg = Format(p.Format, p.Path);
        var (directory, baseName) = Destination(p.Path, p.OutDir);
        var count = (int)Math.Max(1, Math.Min(p.Burst?.Count ?? 1, 1000));
        var superSize = (int)Math.Max(1, Math.Min(p.SuperSize ?? 1, 8));
        var camera = p.Source is "camera" ? CameraOf(p.Camera) : null;
        if (p.Source is not (null or "screen" or "camera"))
        {
            throw ProtocolException.InvalidParams("params.source", "params.source must be screen or camera.");
        }

        var items = new List<CaptureResult>();
        for (var shot = 1; shot <= count; shot++)
        {
            if (shot > 1)
            {
                yield return p.Burst?.EveryMs is { } ms ? PumpWait.Realtime(ms) : PumpWait.Frames((int)Math.Max(1, p.Burst?.EveryFrames ?? 1));
            }

            // Decided before the frame renders: what to crop, what to mark, and whether to hide the overlay.
            var (screenWidth, screenHeight) = capture.ScreenSize;
            var region = Region(p.Region, capture);
            var marks = p.Annotate == "ui-marks" ? UiMarks() : new List<(UiElementFacts Element, long H)>();
            var highlights = p.Annotate == "highlights" && Hooks is { } hooks ? hooks.Highlights.ToList() : new List<(double X, double Y, double W, double H, string Label)>();
            var hidden = (p.HideOverlay ?? true) ? Hooks?.HideOverlay() : null;
            ImagePixels image;
            try
            {
                yield return PumpWait.EndOfFrame;
                image = camera is not null || p.Source == "camera"
                    ? capture.CaptureCamera(camera, screenWidth, screenHeight)
                    : capture.CaptureScreen(superSize);
            }
            finally
            {
                hidden?.Dispose();
            }

            var frame = _pump.Clock.FrameCount;
            var realtimeMs = (long)(_pump.Clock.Realtime * 1000);
            var path = Path.Combine(directory, count == 1 && baseName is not null ? baseName
                : baseName is not null ? $"{Path.GetFileNameWithoutExtension(baseName)}-{shot}{Path.GetExtension(baseName)}"
                : $"shot-{frame}-{shot}.{(jpg ? "jpg" : "png")}");
            var factor = (double)image.Width / Math.Max(1, screenWidth);

            // Crop, scale and annotate off the main thread.
            var work = Task.Run(() => Process(image, region, factor, p.MaxWidth, p.MaxHeight, marks, highlights));
            yield return PumpWait.Until(() => work.IsCompleted, WorkerTimeoutMs);
            var (processed, uiMarks) = work.GetAwaiter().GetResult();

            var encode = jpg ? null : Task.Run(() => PngEncoder.Encode(processed));
            byte[] bytes;
            if (encode is null)
            {
                bytes = capture.EncodeJpg(processed, (int)Math.Max(1, Math.Min(p.Quality ?? 85, 100)));
            }
            else
            {
                yield return PumpWait.Until(() => encode.IsCompleted, WorkerTimeoutMs);
                bytes = encode.GetAwaiter().GetResult();
            }

            var write = Task.Run(() => Write(path, bytes));
            yield return PumpWait.Until(() => write.IsCompleted, WorkerTimeoutMs);
            items.Add(new CaptureResult
            {
                Path = path,
                Width = processed.Width,
                Height = processed.Height,
                OriginalWidth = image.Width,
                OriginalHeight = image.Height,
                Bytes = bytes.Length,
                Sha256 = write.GetAwaiter().GetResult(),
                Frame = frame,
                RealtimeMs = realtimeMs,
                Marks = p.Annotate == "ui-marks" ? uiMarks : null,
                Inline = p.Inline is { } inline && bytes.Length <= inline.MaxBytes ? Convert.ToBase64String(bytes) : null,
            });
        }

        yield return new ScreenshotCaptureResult { Items = items };
    }

    [RpcMethod(Methods.ScreenshotCamera, DefaultTimeoutMs = 30_000, MaxTimeoutMs = 600_000)]
    public IEnumerable<object?> CaptureCamera(RequestContext context, ScreenshotCameraParams p)
    {
        var capture = Capture;
        var jpg = Format(null, p.Path);
        var (directory, baseName) = Destination(p.Path, null);
        var camera = CameraOf(p.Camera);
        var (screenWidth, screenHeight) = capture.ScreenSize;
        yield return PumpWait.EndOfFrame;
        var image = capture.CaptureCamera(camera, (int)Math.Max(1, Math.Min(p.Width ?? screenWidth, 16384)), (int)Math.Max(1, Math.Min(p.Height ?? screenHeight, 16384)));
        var frame = _pump.Clock.FrameCount;
        var realtimeMs = (long)(_pump.Clock.Realtime * 1000);
        byte[] bytes;
        if (jpg)
        {
            bytes = capture.EncodeJpg(image, 85);
        }
        else
        {
            var encode = Task.Run(() => PngEncoder.Encode(image));
            yield return PumpWait.Until(() => encode.IsCompleted, WorkerTimeoutMs);
            bytes = encode.GetAwaiter().GetResult();
        }

        var path = Path.Combine(directory, baseName!);
        var write = Task.Run(() => Write(path, bytes));
        yield return PumpWait.Until(() => write.IsCompleted, WorkerTimeoutMs);
        yield return new CaptureResult
        {
            Path = path, Width = image.Width, Height = image.Height, OriginalWidth = image.Width, OriginalHeight = image.Height,
            Bytes = bytes.Length, Sha256 = write.GetAwaiter().GetResult(), Frame = frame, RealtimeMs = realtimeMs,
        };
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    private static (ImagePixels Image, List<UiMark> Marks) Process(ImagePixels image, (double X, double Y, double W, double H)? region, double factor,
        long? maxWidth, long? maxHeight, List<(UiElementFacts Element, long H)> marks, List<(double X, double Y, double W, double H, string Label)> highlights)
    {
        double cropX = 0, cropY = 0;
        if (region is { } r)
        {
            cropX = r.X * factor;
            cropY = r.Y * factor;
            image = ImageOps.Crop(image, (int)Math.Floor(cropX), (int)Math.Floor(cropY), (int)Math.Ceiling(r.W * factor), (int)Math.Ceiling(r.H * factor))
                ?? throw ProtocolException.InvalidParams("params.region", "The region is outside the screen.");
            cropX = Math.Floor(cropX);
            cropY = Math.Floor(cropY);
        }

        var before = image.Width;
        image = ImageOps.Fit(image, (int?)maxWidth, (int?)maxHeight);
        var scale = factor * image.Width / before;

        (int X, int Y, int W, int H) ToImage(double x, double y, double w, double h) =>
            ((int)Math.Round((x * factor - cropX) * image.Width / before), (int)Math.Round((y * factor - cropY) * image.Width / before),
             (int)Math.Round(w * scale), (int)Math.Round(h * scale));

        var result = new List<UiMark>();
        foreach (var (x, y, w, h, label) in highlights)
        {
            var box = ToImage(x, y, w, h);
            ImageOps.DrawRect(image, box.X, box.Y, box.W, box.H, HighlightColor);
            if (label.Length > 0)
            {
                ImageOps.DrawLabel(image, box.X, box.Y - 18, label, HighlightColor, 0x000000FF);
            }
        }

        var number = 0;
        foreach (var (element, handle) in marks)
        {
            var box = ToImage(element.ScreenRect.X, element.ScreenRect.Y, element.ScreenRect.W, element.ScreenRect.H);
            if (box.W <= 0 || box.H <= 0 || box.X >= image.Width || box.Y >= image.Height || box.X + box.W <= 0 || box.Y + box.H <= 0)
            {
                continue; // outside the captured area
            }

            number++;
            ImageOps.DrawRect(image, box.X, box.Y, box.W, box.H, MarkColor);
            // The number sits just above the box's corner (so it doesn't cover the element's text), or inside when there's no room.
            const int labelHeight = 18;
            ImageOps.DrawLabel(image, box.X, box.Y >= labelHeight ? box.Y - labelHeight : box.Y, number.ToString(CultureInfo.InvariantCulture), MarkColor, 0xFFFFFFFF);
            result.Add(new UiMark
            {
                Mark = number,
                H = handle,
                Kind = element.Kind,
                Text = element.Text,
                Rect = new ScreenRect { X = box.X, Y = box.Y, W = box.W, H = box.H },
            });
        }

        return (image, result);
    }

    // Visible interactable elements, with handles and locators (main thread).
    private List<(UiElementFacts Element, long H)> UiMarks()
    {
        if (_unity.Ui is not { UguiStatus.Available: true } ui)
        {
            return new List<(UiElementFacts, long)>();
        }

        return ui.Snapshot(onlyInteractable: true, onlyVisible: true, includeText: true, MaxMarks)
            .Select(e => (e, _data.Handles.Mint(e.GameObject))).ToList();
    }

    private (double X, double Y, double W, double H)? Region(CaptureRegion? region, ICaptureApi capture)
    {
        if (region is null)
        {
            return null;
        }

        if (region.Target is { } target)
        {
            var resolved = _data.Targets.Resolve(target, "params.region.target");
            if (resolved.IsStatic || resolved.Value is null)
            {
                throw ProtocolException.InvalidParams("params.region.target", "The target must be a GameObject or a component.");
            }

            var rect = capture.ScreenRectOf(resolved.Value) ?? throw ProtocolException.InvalidParams("params.region.target", "The target has nothing on screen (no UI rect, renderer or collider in view).");
            var pad = Math.Max(0, region.Padding ?? 0);
            return (rect.X - pad, rect.Y - pad, rect.W + (2 * pad), rect.H + (2 * pad));
        }

        if (region.X is { } x && region.Y is { } y && region.W is { } w && region.H is { } h && w > 0 && h > 0)
        {
            return (x, y, w, h);
        }

        throw ProtocolException.InvalidParams("params.region", "params.region is {x, y, w, h} (screen pixels, origin top left) or {target, padding}.");
    }

    private object? CameraOf(Target? target)
    {
        if (target is null)
        {
            return null;
        }

        var resolved = _data.Targets.Resolve(target, "params.camera");
        return resolved.IsStatic ? throw ProtocolException.InvalidParams("params.camera", "params.camera must be a camera object.") : resolved.Value;
    }

    private static bool Format(string? format, string? path) => format switch
    {
        "jpg" => true,
        "png" => false,
        null => path is not null && (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)),
        _ => throw ProtocolException.InvalidParams("params.format", "params.format must be png or jpg."),
    };

    // Exactly one of path and outDir: absolute, into an existing directory (nothing is created).
    private static (string Directory, string? FileName) Destination(string? path, string? outDir)
    {
        if ((path is null) == (outDir is null))
        {
            throw ProtocolException.InvalidParams("params.path", "Give exactly one of params.path and params.outDir.");
        }

        var target = path ?? outDir!;
        var param = path is null ? "params.outDir" : "params.path";
        if (!System.IO.Path.IsPathRooted(target))
        {
            throw ProtocolException.InvalidParams(param, $"{param} must be an absolute path.");
        }

        var directory = path is null ? outDir! : System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        if (!Directory.Exists(directory))
        {
            throw ProtocolException.InvalidParams(param, $"The directory {directory} doesn't exist.");
        }

        return (directory, path is null ? null : System.IO.Path.GetFileName(path));
    }

    // Writes through a temporary file (so a reader never sees half an image); returns the SHA-256.
    private static string Write(string path, byte[] bytes)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
        }
        catch (Exception e)
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
            }

            throw new ProtocolException(ErrorCodes.IoFailed, $"Writing {path} failed: {e.Message}", AgentErrors.Data(("path", new UnityLudometry.Protocol.Json.JsonString(path))));
        }

        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }
}
