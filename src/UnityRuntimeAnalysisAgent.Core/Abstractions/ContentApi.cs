using System;
using System.Collections.Generic;

namespace UnityRuntimeAnalysisAgent.Core.Abstractions;

/// <summary>
/// What Core needs from Unity for content: type-specific summaries, AssetBundles, <c>Resources</c>, reading pixels
/// back, and text assets' bytes, for export. Main thread only unless noted. The Unity bindings implement it.
/// </summary>
public interface IContentApi
{
    /// <summary>Type-specific facts about a loaded object (a texture's size and format, a clip's length, …), keyed by
    /// field name, or null when there are none for its type. Values are plain values, Unity structs or other Unity objects.</summary>
    IDictionary<string, object?>? Summary(object unityObject);

    /// <summary>The object's estimated memory (<c>Profiler.GetRuntimeMemorySizeLong</c>), or null when unavailable.</summary>
    long? RuntimeMemorySize(object unityObject);

    /// <summary>The object's <c>hideFlags</c>.</summary>
    int HideFlags(object unityObject);

    /// <summary><c>AssetBundle.GetAllLoadedAssetBundles()</c>.</summary>
    IReadOnlyList<object> LoadedBundles();

    /// <summary>A loaded bundle's name and contents.</summary>
    BundleFacts DescribeBundle(object bundle, bool includeAssetNames);

    /// <summary><c>AssetBundle.LoadAsset(name, type)</c>, or null.</summary>
    object? LoadFromBundle(object bundle, string assetName, Type? type);

    /// <summary><c>Resources.Load(path, type)</c>, or null.</summary>
    object? ResourcesLoad(string path, Type? type);

    /// <summary><c>Resources.LoadAll(path, type)</c>.</summary>
    IReadOnlyList<object> ResourcesLoadAll(string path, Type? type);

    /// <summary>The pixels of a texture, sprite (cropped to its rect) or render texture as RGBA32, rows bottom to top (read
    /// back through the GPU, so unreadable textures work too), or null when the object has no pixels.</summary>
    ImagePixels? ReadPixels(object image);

    /// <summary>A <c>TextAsset</c>'s bytes.</summary>
    byte[]? TextAssetBytes(object textAsset);

    /// <summary>The Addressables runtime, or null when the game doesn't use it (<see cref="AddressablesStatus"/> says why).</summary>
    IAddressablesApi? Addressables { get; }

    /// <summary>Whether Addressables is available, its version, and why not. May be called from any thread.</summary>
    ModuleStatus AddressablesStatus { get; }
}

/// <summary>An optional module's availability.</summary>
public sealed class ModuleStatus
{
    /// <summary>Creates the status.</summary>
    public ModuleStatus(bool available, string? version, string? reason)
    {
        Available = available;
        Version = version;
        Reason = reason;
    }

    /// <summary>Whether the module was found.</summary>
    public bool Available { get; }

    /// <summary>Its version, if known.</summary>
    public string? Version { get; }

    /// <summary>Why it's unavailable.</summary>
    public string? Reason { get; }
}

/// <summary>The Addressables runtime (reflection-bound). Main thread only.</summary>
public interface IAddressablesApi
{
    /// <summary>The package version, if exposed.</summary>
    string? Version { get; }

    /// <summary>The runtime path (<c>Addressables.RuntimePath</c>), if exposed.</summary>
    string? RuntimePath { get; }

    /// <summary>The ids of the resource locators (catalogs).</summary>
    IReadOnlyList<string> LocatorIds();

    /// <summary>The keys of one locator (null: every locator).</summary>
    IEnumerable<object> Keys(string? locatorId);

    /// <summary>The locations of a key (optionally only those loadable as <paramref name="type"/>).</summary>
    IReadOnlyList<AddressableLocationFacts> Locate(object key, Type? type, string? locatorId);

    /// <summary>Starts <c>LoadAssetAsync&lt;type&gt;(key)</c> and returns the operation handle.</summary>
    object LoadAsync(object key, Type type);

    /// <summary>An operation's state.</summary>
    AddressablesOperation Poll(object operation);

    /// <summary><c>Addressables.Release(operation)</c>.</summary>
    void Release(object operation);
}

/// <summary>One location of an Addressables key.</summary>
public sealed class AddressableLocationFacts
{
    /// <summary>The location's internal id (a path or URL).</summary>
    public string InternalId { get; set; } = string.Empty;

    /// <summary>The provider that loads it.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>The type it loads as (full name).</summary>
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>The internal ids of its dependencies.</summary>
    public List<string> Dependencies { get; set; } = new();

    /// <summary>Its primary key.</summary>
    public string PrimaryKey { get; set; } = string.Empty;
}

/// <summary>The state of an Addressables operation.</summary>
public readonly struct AddressablesOperation
{
    /// <summary>Creates the state.</summary>
    public AddressablesOperation(bool done, bool succeeded, object? result, string? error)
    {
        Done = done;
        Succeeded = succeeded;
        Result = result;
        Error = error;
    }

    /// <summary>Whether it finished.</summary>
    public bool Done { get; }

    /// <summary>Whether it finished successfully.</summary>
    public bool Succeeded { get; }

    /// <summary>What it loaded.</summary>
    public object? Result { get; }

    /// <summary>Why it failed.</summary>
    public string? Error { get; }
}

/// <summary>A loaded AssetBundle.</summary>
public sealed class BundleFacts
{
    /// <summary>The bundle's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether it holds scenes.</summary>
    public bool IsStreamedSceneAssetBundle { get; set; }

    /// <summary>Its asset names (when asked for).</summary>
    public List<string>? AssetNames { get; set; }

    /// <summary>Its scene paths (scene bundles).</summary>
    public List<string>? ScenePaths { get; set; }
}

/// <summary>Pixels as RGBA32, rows bottom to top (Unity's order).</summary>
public sealed class ImagePixels
{
    /// <summary>Creates the image.</summary>
    public ImagePixels(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>4 bytes per pixel, rows bottom to top.</summary>
    public byte[] Rgba { get; }
}
