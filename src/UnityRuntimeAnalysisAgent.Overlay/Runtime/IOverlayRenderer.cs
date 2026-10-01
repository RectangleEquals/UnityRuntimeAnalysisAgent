using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityRuntimeAnalysisAgent.Core.Hosting;
using UnityRuntimeAnalysisAgent.Core.Overlay;
using UnityRuntimeAnalysisAgent.Overlay.Assets;
using UnityRuntimeAnalysisAgent.Overlay.Views;

namespace UnityRuntimeAnalysisAgent.Overlay.Runtime;

/// <summary>What a renderer gets to draw with: the model, the theme, the loaded bundle and the log.</summary>
public sealed class OverlayContext
{
    /// <summary>Creates the context.</summary>
    public OverlayContext(OverlayController controller, Theme theme, LoadedBundle? bundle, string overlayDir, IAgentLogger log, Func<double> now)
    {
        Controller = controller;
        Theme = theme;
        Bundle = bundle;
        OverlayDir = overlayDir;
        Log = log;
        Now = now;
    }

    /// <summary>The overlay's model.</summary>
    public OverlayController Controller { get; }

    /// <summary>The theme in use.</summary>
    public Theme Theme { get; }

    /// <summary>The game's bundle family, if one loaded (fonts, shaders and, except legacy, the UI Toolkit theme).</summary>
    public LoadedBundle? Bundle { get; }

    /// <summary>The <c>overlay/</c> folder (views, themes, icons).</summary>
    public string OverlayDir { get; }

    /// <summary>The agent's log.</summary>
    public IAgentLogger Log { get; }

    /// <summary>Unscaled realtime in seconds (overlay timing never follows the game's time scale).</summary>
    public Func<double> Now { get; }
}

/// <summary>
/// One way of drawing the overlay: UI Toolkit, styled uGUI, or the IMGUI emergency view. The runtime tries them in order
/// and keeps the first that starts; a renderer whose objects the game destroyed is started again.
/// </summary>
public interface IOverlayRenderer
{
    /// <summary>Its protocol name: <c>uitoolkit</c>, <c>ugui</c> or <c>imgui</c>.</summary>
    string Name { get; }

    /// <summary>Starts drawing. Returns false with the reason when it can't (the next renderer is tried).</summary>
    bool TryStart(OverlayContext context, out string? reason);

    /// <summary>Whether its objects still exist (a game can destroy everything on a scene load).</summary>
    bool Alive { get; }

    /// <summary>
    /// Set when it found, after starting, that it can't draw correctly (e.g. UI Toolkit's theme didn't apply); the runtime
    /// then stops it and tries the next renderer.
    /// </summary>
    string? Failure { get; }

    /// <summary>Where it draws, in screen pixels from the top-left (pointer hover and the click blocker use them).</summary>
    IReadOnlyList<Rect> Occupied { get; }

    /// <summary>Called every frame on the main thread: follows the model (state, tab data, toasts, prompts, highlights).</summary>
    void Update();

    /// <summary>Removes everything it created.</summary>
    void Stop();
}

/// <summary>An asset bundle family, loaded, with its assets found by name and type.</summary>
public sealed class LoadedBundle : IDisposable
{
    private readonly AssetBundle _bundle;
    private readonly UnityEngine.Object[] _assets;

    private LoadedBundle(BundleFamily family, AssetBundle bundle)
    {
        Family = family;
        _bundle = bundle;
        _assets = bundle.LoadAllAssets();
    }

    /// <summary>The family.</summary>
    public BundleFamily Family { get; }

    /// <summary>A font by its file name (without extension), or null.</summary>
    public Font? Font(string name) => _assets.OfType<Font>().FirstOrDefault(f => f.name == name);

    /// <summary>A shader by its full name, or null.</summary>
    public Shader? Shader(string name) => _assets.OfType<Shader>().FirstOrDefault(s => s.name == name);

    /// <summary>An asset by its type's full name (for UI Toolkit types this assembly can't name), or null.</summary>
    public UnityEngine.Object? OfType(string typeFullName, string? name = null) =>
        _assets.FirstOrDefault(a => a != null && a.GetType().FullName == typeFullName && (name is null || a.name == name));

    /// <summary>Loads the family serving the running Unity version, after checking the file against the manifest.</summary>
    /// <exception cref="InvalidDataException">The bundle is missing, changed, or Unity can't load it.</exception>
    public static LoadedBundle Load(OverlayAssets assets, string unityVersion)
    {
        var (family, why) = assets.Pick(unityVersion);
        if (family is null)
        {
            throw new InvalidDataException(why);
        }

        var bundle = AssetBundle.LoadFromFile(assets.Verified(family));
        if (bundle == null)
        {
            throw new InvalidDataException($"Unity couldn't load the overlay bundle {family.File}.");
        }

        return new LoadedBundle(family, bundle);
    }

    /// <inheritdoc />
    public void Dispose() => _bundle.Unload(true);
}

/// <summary>Where the overlay's files are: <c>overlay/</c> next to the agent's assemblies.</summary>
public static class OverlayFiles
{
    /// <summary>The overlay folder next to this assembly.</summary>
    public static string Directory => Path.Combine(Path.GetDirectoryName(typeof(OverlayFiles).Assembly.Location) ?? ".", "overlay");

    /// <summary>A theme by name from <c>themes/</c>, falling back to <c>default</c> (with a warning) when it's missing.</summary>
    public static Theme LoadTheme(string overlayDir, string name, IAgentLogger log)
    {
        var path = Path.Combine(Path.Combine(overlayDir, "themes"), name + ".json");
        if (!File.Exists(path) && name != "default")
        {
            log.Warning($"Overlay theme '{name}' not found ({path}); using the default theme.");
            path = Path.Combine(Path.Combine(overlayDir, "themes"), "default.json");
        }

        var theme = Theme.Load(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
        foreach (var warning in theme.Warnings)
        {
            log.Warning($"Overlay theme '{theme.Name}': {warning}");
        }

        return theme;
    }

    /// <summary>Every view in <c>views/</c> by name; views that can't be read are skipped with a warning.</summary>
    public static IReadOnlyDictionary<string, ViewDocument> LoadViews(string overlayDir, IAgentLogger log)
    {
        var views = new Dictionary<string, ViewDocument>(StringComparer.Ordinal);
        var dir = Path.Combine(overlayDir, "views");
        if (!System.IO.Directory.Exists(dir))
        {
            return views;
        }

        foreach (var file in System.IO.Directory.GetFiles(dir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var view = ViewLoader.Load(name, File.ReadAllText(file));
                foreach (var warning in view.Warnings)
                {
                    log.Warning($"Overlay view '{name}': {warning}");
                }

                views[name] = view;
            }
            catch (FormatException e)
            {
                log.Warning($"Overlay view '{name}' skipped: {e.Message}");
            }
        }

        return views;
    }
}
