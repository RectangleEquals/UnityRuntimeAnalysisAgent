using System;
using System.Collections.Generic;

namespace UnityRuntimeAnalysisAgent.Core.Hosting;

/// <summary>Reads agent configuration values (the loader's config file in the game; a dictionary in tools).</summary>
public interface IConfigSource
{
    /// <summary>The raw value of a key such as <c>Transport.Mode</c>, or <c>null</c> when unset.</summary>
    string? Get(string key);
}

/// <summary>An in-memory configuration (tools).</summary>
public sealed class DictionaryConfigSource : IConfigSource
{
    private readonly Dictionary<string, string> _values;

    /// <summary>Creates a source from key/value pairs (keys are case-insensitive).</summary>
    public DictionaryConfigSource(IDictionary<string, string>? values = null)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is not null)
        {
            foreach (var pair in values)
            {
                _values[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>Sets a value.</summary>
    public DictionaryConfigSource Set(string key, string value)
    {
        _values[key] = value;
        return this;
    }

    /// <inheritdoc />
    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
}
