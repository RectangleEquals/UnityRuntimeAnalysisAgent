using System;
using System.Collections.Generic;

namespace UnityRuntimeAnalysisAgent.Core.Abstractions;

/// <summary>A loaded scene (or the DontDestroyOnLoad pseudo-scene), as the Unity bindings read it.</summary>
public sealed class SceneFacts
{
    /// <summary><c>Scene.handle</c>.</summary>
    public int Handle { get; set; }

    /// <summary><c>Scene.name</c> (<c>DontDestroyOnLoad</c> for the pseudo-scene).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>Scene.path</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary><c>Scene.buildIndex</c> (-1 when not in the build settings).</summary>
    public int BuildIndex { get; set; } = -1;

    /// <summary><c>Scene.isLoaded</c>.</summary>
    public bool IsLoaded { get; set; }

    /// <summary>Whether it's the active scene.</summary>
    public bool IsActive { get; set; }

    /// <summary><c>Scene.rootCount</c>.</summary>
    public int RootCount { get; set; }

    /// <summary>Whether it's the DontDestroyOnLoad pseudo-scene.</summary>
    public bool IsDontDestroyOnLoad { get; set; }
}

/// <summary>A GameObject's state, read in one go on the main thread.</summary>
public sealed class GameObjectFacts
{
    /// <summary>The GameObject.</summary>
    public object GameObject { get; set; } = null!;

    /// <summary><c>name</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>activeSelf</c>.</summary>
    public bool ActiveSelf { get; set; }

    /// <summary><c>activeInHierarchy</c>.</summary>
    public bool ActiveInHierarchy { get; set; }

    /// <summary><c>tag</c>.</summary>
    public string Tag { get; set; } = "Untagged";

    /// <summary><c>layer</c>.</summary>
    public int Layer { get; set; }

    /// <summary><c>hideFlags</c>.</summary>
    public string HideFlags { get; set; } = "None";

    /// <summary>The scene's name (<c>ddol</c> for DontDestroyOnLoad).</summary>
    public string Scene { get; set; } = string.Empty;

    /// <summary>The path from the scene root, with <c>Name[n]</c> where siblings share a name.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The parent GameObject, or null at the root.</summary>
    public object? Parent { get; set; }

    /// <summary><c>transform.GetSiblingIndex()</c>.</summary>
    public int SiblingIndex { get; set; }

    /// <summary>Child GameObjects, in order.</summary>
    public IReadOnlyList<object> Children { get; set; } = Array.Empty<object>();

    /// <summary>Components, in order, with <c>enabled</c> where the component has one.</summary>
    public IReadOnlyList<(object Component, bool? Enabled)> Components { get; set; } = Array.Empty<(object, bool?)>();

    /// <summary>Transform values (boxed Unity structs).</summary>
    public object? LocalPosition { get; set; }

    /// <summary><c>localRotation</c>.</summary>
    public object? LocalRotation { get; set; }

    /// <summary><c>localScale</c>.</summary>
    public object? LocalScale { get; set; }

    /// <summary><c>position</c>.</summary>
    public object? Position { get; set; }

    /// <summary><c>rotation</c>.</summary>
    public object? Rotation { get; set; }

    /// <summary><c>lossyScale</c>.</summary>
    public object? LossyScale { get; set; }
}

/// <summary>A scene load, unload or active-scene change.</summary>
public sealed class SceneChange
{
    /// <summary><c>loaded</c>, <c>unloaded</c> or <c>activeChanged</c>.</summary>
    public string Change { get; set; } = string.Empty;

    /// <summary>The scene.</summary>
    public SceneFacts Scene { get; set; } = new();

    /// <summary>For <c>activeChanged</c>: the previous active scene's name.</summary>
    public string? PreviousActive { get; set; }

    /// <summary>For <c>loaded</c>: <c>single</c> or <c>additive</c>.</summary>
    public string? Mode { get; set; }
}
