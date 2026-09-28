using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Dispatch;

namespace UnityRuntimeAnalysisAgent.Core.Data;

/// <summary>Compares objects by reference (live objects must never be matched by their own <c>Equals</c>).</summary>
internal sealed class IdentityComparer : IEqualityComparer<object>
{
    public static readonly IdentityComparer Instance = new();

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}

/// <summary>Unity types recognised by full name: Core never references UnityEngine.</summary>
public static class UnityTypes
{
    private static readonly ConcurrentDictionary<Type, bool> UnityObjects = new();
    private static readonly ConcurrentDictionary<Type, bool> UnityEvents = new();

    /// <summary>Whether <paramref name="type"/> is <c>UnityEngine.Object</c> or derives from it.</summary>
    public static bool IsUnityObject(Type type) => UnityObjects.GetOrAdd(type, t => DerivesFrom(t, "UnityEngine.Object"));

    /// <summary>Whether <paramref name="type"/> is a <c>UnityEngine.Events.UnityEventBase</c>.</summary>
    public static bool IsUnityEvent(Type type) => UnityEvents.GetOrAdd(type, t => DerivesFrom(t, "UnityEngine.Events.UnityEventBase"));

    /// <summary>Whether <paramref name="type"/> is a GameObject or a Component (things that live in the scene hierarchy).</summary>
    public static bool IsSceneObject(Type type) => type.FullName == "UnityEngine.GameObject" || DerivesFrom(type, "UnityEngine.Component");

    /// <summary>Types whose own fields are Unity's bookkeeping, not game state (skipped when a Unity object is expanded).</summary>
    public static bool IsEngineBase(Type type) => type.FullName is "UnityEngine.Object" or "UnityEngine.Component" or "UnityEngine.Behaviour"
        or "UnityEngine.MonoBehaviour" or "UnityEngine.ScriptableObject";

    private static bool DerivesFrom(Type? type, string fullName)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (type.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The data model's errors (codes and data as the protocol defines them).</summary>
public static class DataErrors
{
    /// <summary><c>INDEX_STALE</c>: the anchor's module isn't loaded, or its token doesn't resolve in the loaded build.</summary>
    public static ProtocolException IndexStale(Anchor anchor, string message, IEnumerable<(string Assembly, string Mvid)> loadedBuilds)
    {
        var data = new JsonObject
        {
            { "anchor", anchor.ToJson() },
            { "hint", JsonValue.From("The code this anchor was taken from isn't what's loaded now: re-read it from the current build.") },
        };
        var builds = loadedBuilds.ToList();
        if (builds.Count > 0)
        {
            data.Add("loadedBuildsOfAssembly", new JsonArray(builds.Select(b => (JsonValue?)new JsonObject { { "assembly", JsonValue.From(b.Assembly) }, { "mvid", JsonValue.From(b.Mvid) } })));
        }

        return AgentErrors.Create(ErrorCodes.IndexStale, message, data);
    }

    /// <summary><c>HANDLE_EXPIRED</c>, with what the handle last referred to when known.</summary>
    public static ProtocolException HandleExpired(long h, string message, string? lastType = null, string? lastName = null)
    {
        var data = new JsonObject { { "h", JsonValue.From(h) } };
        if (lastType is not null)
        {
            var last = new JsonObject { { "type", JsonValue.From(lastType) } };
            if (lastName is not null)
            {
                last.Add("name", JsonValue.From(lastName));
            }

            data.Add("last", last);
        }

        return AgentErrors.Create(ErrorCodes.HandleExpired, message, data);
    }

    /// <summary><c>REF_EXPIRED</c>: the ref was evicted (or is from another run); use the locator instead.</summary>
    public static ProtocolException RefExpired(string reference, string? locator)
    {
        var data = new JsonObject { { "hint", JsonValue.From(locator is null ? "Read the value again." : "Resolve the stub's locator with locator.resolve.") } };
        if (locator is not null)
        {
            data.Add("locator", JsonValue.From(locator));
        }

        return AgentErrors.Create(ErrorCodes.RefExpired, $"The ref '{reference}' has expired.", data);
    }

    /// <summary><c>AMBIGUOUS</c>: an exploratory name matched several members.</summary>
    public static ProtocolException Ambiguous(string message, IEnumerable<Anchor> candidates) =>
        AgentErrors.Create(ErrorCodes.Ambiguous, message, new JsonObject { { "candidates", new JsonArray(candidates.Select(c => (JsonValue?)c.ToJson())) } });

    /// <summary><c>INVALID_PARAMS</c> for a value that can't be converted: <c>{param, expectedType, got}</c>.</summary>
    public static ProtocolException InvalidValue(string param, string expectedType, JsonValue? got, string? why = null) =>
        AgentErrors.Create(ErrorCodes.InvalidParams, $"{param}: can't use {Describe(got)} as {expectedType}{(why is null ? "." : ": " + why)}", new JsonObject
        {
            { "param", JsonValue.From(param) },
            { "expectedType", JsonValue.From(expectedType) },
            { "got", got ?? JsonNull.Instance },
        });

    /// <summary><c>INVALID_PARAMS</c> naming the parameter.</summary>
    public static ProtocolException InvalidParams(string param, string message) => ProtocolException.InvalidParams(param, message);

    /// <summary><c>NOT_FOUND</c> naming the parameter.</summary>
    public static ProtocolException NotFound(string param, string message) =>
        AgentErrors.Create(ErrorCodes.NotFound, message, new JsonObject { { "param", JsonValue.From(param) } });

    /// <summary><c>UNSUPPORTED</c>: something this build (or this game) can't do yet.</summary>
    public static ProtocolException Unsupported(string message) => AgentErrors.Create(ErrorCodes.Unsupported, message);

    private static string Describe(JsonValue? value) => value switch
    {
        null or JsonNull => "null",
        JsonString => "a string",
        JsonNumber => "a number",
        JsonBoolean => "a boolean",
        JsonArray => "an array",
        _ => "an object",
    };
}
