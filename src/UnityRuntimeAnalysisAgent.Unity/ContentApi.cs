using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityRuntimeAnalysisAgent.Core.Abstractions;
using Object = UnityEngine.Object;

namespace UnityRuntimeAnalysisAgent.Unity;

/// <summary>
/// Content for Core: type-specific summaries, AssetBundles, <c>Resources</c>, reading pixels back, and text assets' bytes, for export. Compiled against the Unity 2018.1 API; members added later are read by name. Main thread only.
/// </summary>
public sealed class ContentApi : IContentApi
{
    private readonly AddressablesBinder _addressables = new();

    /// <inheritdoc />
    public IAddressablesApi? Addressables => _addressables.EnsureBound() ? _addressables : null;

    /// <inheritdoc />
    public ModuleStatus AddressablesStatus
    {
        get
        {
            _addressables.EnsureBound();
            return new ModuleStatus(_addressables.Available, _addressables.Version, _addressables.Reason);
        }
    }

    /// <inheritdoc />
    public IDictionary<string, object?>? Summary(object unityObject)
    {
        switch (unityObject)
        {
            case Sprite sprite:
                return new Dictionary<string, object?>
                {
                    { "rect", Rect(sprite.rect) },
                    { "pivot", new[] { sprite.pivot.x, sprite.pivot.y } },
                    { "pixelsPerUnit", sprite.pixelsPerUnit },
                    { "texture", sprite.texture },
                    { "packed", sprite.packed },
                    { "border", new[] { sprite.border.x, sprite.border.y, sprite.border.z, sprite.border.w } },
                };
            case Texture texture:
                var facts = new Dictionary<string, object?>
                {
                    { "width", texture.width },
                    { "height", texture.height },
                    { "dimension", texture.dimension },
                    { "filterMode", texture.filterMode },
                    { "wrapMode", texture.wrapMode },
                    { "isReadable", Member(texture, "isReadable") },
                    { "mipmapCount", Member(texture, "mipmapCount") },
                };
                if (texture is Texture2D texture2D)
                {
                    facts["format"] = texture2D.format;
                }
                else if (texture is RenderTexture renderTexture)
                {
                    facts["format"] = renderTexture.format;
                    facts["depth"] = renderTexture.depth;
                }

                return facts;
            case Font font:
                return new Dictionary<string, object?>
                {
                    { "fontNames", font.fontNames },
                    { "fontSize", font.fontSize },
                    { "dynamic", font.dynamic },
                    { "characterCount", font.characterInfo?.Length ?? 0 },
                };
            case AudioClip clip:
                return new Dictionary<string, object?>
                {
                    { "length", clip.length },
                    { "channels", clip.channels },
                    { "frequency", clip.frequency },
                    { "samples", clip.samples },
                    { "loadType", clip.loadType },
                    { "loadState", clip.loadState },
                    { "preloadAudioData", clip.preloadAudioData },
                };
            case Mesh mesh:
                return new Dictionary<string, object?>
                {
                    { "vertexCount", mesh.vertexCount },
                    { "subMeshCount", mesh.subMeshCount },
                    { "bounds", new Dictionary<string, object?> { { "center", Vector(mesh.bounds.center) }, { "size", Vector(mesh.bounds.size) } } },
                    { "isReadable", mesh.isReadable },
                    { "blendShapeCount", mesh.blendShapeCount },
                };
            case Material material:
                return new Dictionary<string, object?>
                {
                    { "shader", material.shader != null ? material.shader.name : null },
                    { "renderQueue", material.renderQueue },
                    { "keywords", material.shaderKeywords },
                    { "textures", MaterialTextures(material) },
                };
            case Shader shader:
                return new Dictionary<string, object?> { { "name", shader.name }, { "isSupported", shader.isSupported }, { "passCount", Member(shader, "passCount") } };
            case AnimationClip animation:
                return new Dictionary<string, object?>
                {
                    { "length", animation.length },
                    { "frameRate", animation.frameRate },
                    { "legacy", animation.legacy },
                    { "eventCount", animation.events?.Length ?? 0 },
                };
            case RuntimeAnimatorController controller:
                return new Dictionary<string, object?> { { "clips", controller.animationClips.Where(c => c != null).Select(c => c.name).Distinct().ToList() } };
            case TextAsset textAsset:
                var bytes = textAsset.bytes ?? Array.Empty<byte>();
                var looksLikeText = Array.IndexOf(bytes, (byte)0) < 0;
                var text = looksLikeText ? textAsset.text ?? string.Empty : string.Empty;
                return new Dictionary<string, object?>
                {
                    { "byteLength", bytes.Length },
                    { "looksLikeText", looksLikeText },
                    { "preview", looksLikeText ? (text.Length > 256 ? text.Substring(0, 256) : text) : null },
                };
            case GameObject gameObject:
                return new Dictionary<string, object?>
                {
                    { "components", gameObject.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().FullName).ToList() },
                    { "childCount", gameObject.transform.childCount },
                    { "isPrefabAsset", !gameObject.scene.IsValid() },
                };
        }

        return unityObject.GetType().FullName == "TMPro.TMP_FontAsset" ? TmpFontAsset(unityObject) : null;
    }

    /// <inheritdoc />
    public long? RuntimeMemorySize(object unityObject) => unityObject is Object o ? Profiler.GetRuntimeMemorySizeLong(o) : null;

    /// <inheritdoc />
    public int HideFlags(object unityObject) => unityObject is Object o ? (int)o.hideFlags : 0;

    /// <inheritdoc />
    public IReadOnlyList<object> LoadedBundles() => AssetBundle.GetAllLoadedAssetBundles().Where(b => b != null).Cast<object>().ToList();

    /// <inheritdoc />
    public BundleFacts DescribeBundle(object bundle, bool includeAssetNames)
    {
        var assetBundle = (AssetBundle)bundle;
        var scenes = assetBundle.isStreamedSceneAssetBundle;
        return new BundleFacts
        {
            Name = assetBundle.name,
            IsStreamedSceneAssetBundle = scenes,
            AssetNames = includeAssetNames && !scenes ? assetBundle.GetAllAssetNames().ToList() : null,
            ScenePaths = scenes ? assetBundle.GetAllScenePaths().ToList() : null,
        };
    }

    /// <inheritdoc />
    public object? LoadFromBundle(object bundle, string assetName, Type? type)
    {
        var loaded = ((AssetBundle)bundle).LoadAsset(assetName, type ?? typeof(Object));
        return loaded != null ? loaded : null;
    }

    /// <inheritdoc />
    public object? ResourcesLoad(string path, Type? type)
    {
        var loaded = Resources.Load(path, type ?? typeof(Object));
        return loaded != null ? loaded : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<object> ResourcesLoadAll(string path, Type? type) =>
        Resources.LoadAll(path, type ?? typeof(Object)).Where(o => o != null).Cast<object>().ToList();

    /// <inheritdoc />
    public IImageReadback? BeginReadback(object image)
    {
        Texture? texture;
        UnityEngine.Rect area;
        switch (image)
        {
            case Sprite sprite:
                texture = sprite.texture;
                area = sprite.textureRect;
                break;
            case Texture2D or RenderTexture:
                texture = (Texture)image;
                area = new UnityEngine.Rect(0, 0, texture.width, texture.height);
                break;
            default:
                return null; // cubemaps, 3D and array textures have no single image
        }

        if (texture == null || area.width < 1 || area.height < 1)
        {
            return null;
        }

        if (texture is Texture2D readable && Member(readable, "isReadable") is true && IsPlainFormat(readable.format))
        {
            return new CpuReadback(readable, area);
        }

        return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ? null : new GpuReadback(texture, area);
    }

    /// <inheritdoc />
    public byte[]? TextAssetBytes(object textAsset) => ((TextAsset)textAsset).bytes;

    private static ImagePixels Crop(int sourceWidth, Color32[] pixels, int x, int y, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var pixel = pixels[((y + row) * sourceWidth) + x + column];
                var i = ((row * width) + column) * 4;
                rgba[i] = pixel.r;
                rgba[i + 1] = pixel.g;
                rgba[i + 2] = pixel.b;
                rgba[i + 3] = pixel.a;
            }
        }

        return new ImagePixels(width, height, rgba);
    }

    // A readable texture: one native copy of its pixels in the frame; cropping and conversion happen on the worker.
    private sealed class CpuReadback : IImageReadback
    {
        private readonly Texture2D _texture;
        private readonly int _x;
        private readonly int _y;
        private Color32[]? _pixels;

        public CpuReadback(Texture2D texture, UnityEngine.Rect area)
        {
            _texture = texture;
            (_x, _y, Width, Height) = ((int)area.x, (int)area.y, (int)area.width, (int)area.height);
        }

        public int Width { get; }

        public int Height { get; }

        public bool Step()
        {
            _pixels = _texture.GetPixels32();
            return true;
        }

        public ImagePixels Finish() => Crop(_texture.width, _pixels!, _x, _y, Width, Height);

        public void Dispose() => _pixels = null;
    }

    // Any texture, with a GPU: one copy into a temporary render target, then strips of about a million pixels read back
    // per step (a large texture spreads over several frames), copied as raw RGBA32 without a per-pixel loop.
    private sealed class GpuReadback : IImageReadback
    {
        private const int PixelsPerStep = 1 << 20;
        private readonly RenderTexture _target;
        private readonly Texture2D _strip;
        private readonly byte[] _rgba;
        private readonly int _x;
        private readonly int _y;
        private readonly int _rowsPerStep;
        private int _row;

        public GpuReadback(Texture texture, UnityEngine.Rect area)
        {
            (_x, _y, Width, Height) = ((int)area.x, (int)area.y, (int)area.width, (int)area.height);
            _rowsPerStep = Math.Max(1, Math.Min(Height, PixelsPerStep / Width));
            _rgba = new byte[Width * Height * 4];
            _target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            Graphics.Blit(texture, _target);
            _strip = new Texture2D(Width, _rowsPerStep, TextureFormat.RGBA32, false);
        }

        public int Width { get; }

        public int Height { get; }

        public bool Step()
        {
            var rows = Math.Min(_rowsPerStep, Height - _row);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = _target;
                _strip.ReadPixels(new UnityEngine.Rect(_x, _y + _row, Width, rows), 0, 0, false);
                Buffer.BlockCopy(_strip.GetRawTextureData(), 0, _rgba, _row * Width * 4, rows * Width * 4);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            _row += rows;
            return _row >= Height;
        }

        public ImagePixels Finish() => new(Width, Height, _rgba);

        public void Dispose()
        {
            RenderTexture.ReleaseTemporary(_target);
            Object.DestroyImmediate(_strip);
        }
    }

    // Formats GetPixels32 returns exactly; anything else (compressed, HDR) goes through the GPU and comes back as RGBA32.
    private static bool IsPlainFormat(TextureFormat format) =>
        format is TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.RGB24 or TextureFormat.BGRA32 or TextureFormat.Alpha8 or TextureFormat.R8;

    private static Dictionary<string, object?> MaterialTextures(Material material)
    {
        var textures = new Dictionary<string, object?>();
        if (material.GetType().GetMethod("GetTexturePropertyNames", Type.EmptyTypes)?.Invoke(material, null) is string[] names)
        {
            foreach (var name in names)
            {
                var texture = material.GetTexture(name);
                textures[name] = texture != null ? texture : null;
            }
        }
        else if (material.mainTexture != null)
        {
            textures["_MainTex"] = material.mainTexture;
        }

        return textures;
    }

    // TextMeshPro font assets (by name: the package is optional, and its members differ between versions).
    private static Dictionary<string, object?> TmpFontAsset(object font)
    {
        var face = Member(font, "faceInfo") ?? Member(font, "fontInfo");
        var atlases = Member(font, "atlasTextures") is IEnumerable many ? many.Cast<object?>().Where(t => t is Object o && o != null).ToList()
            : Member(font, "atlas") is Object single && single != null ? new List<object?> { single } : new List<object?>();
        return new Dictionary<string, object?>
        {
            { "family", face is null ? null : Member(face, "familyName") ?? Member(face, "Name") },
            { "style", face is null ? null : Member(face, "styleName") },
            { "pointSize", face is null ? null : Member(face, "pointSize") ?? Member(face, "PointSize") },
            { "atlasTextures", atlases },
            { "glyphCount", (Member(font, "glyphTable") as ICollection)?.Count },
            { "characterCount", (Member(font, "characterTable") as ICollection)?.Count },
            { "fallbacks", (Member(font, "fallbackFontAssetTable") as IEnumerable)?.Cast<object?>().Where(f => f is Object o && o != null).Select(f => ((Object)f!).name).ToList() },
        };
    }

    // A property or field by name (members that only newer Unity versions or optional packages have).
    private static object? Member(object target, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
        try
        {
            var type = target.GetType();
            if (type.GetProperty(name, flags) is { CanRead: true } property && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(target, null);
            }

            return type.GetField(name, flags)?.GetValue(target);
        }
        catch (Exception e) when (e is TargetInvocationException or ArgumentException or MemberAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> Rect(UnityEngine.Rect r) =>
        new() { { "x", r.x }, { "y", r.y }, { "width", r.width }, { "height", r.height } };

    private static float[] Vector(Vector3 v) => new[] { v.x, v.y, v.z };
}
