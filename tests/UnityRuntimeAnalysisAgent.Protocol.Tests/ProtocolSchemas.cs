using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace UnityRuntimeAnalysisAgent.Protocol.Tests;

/// <summary>Locates the pinned protocol folder and validates JSON against its schemas.</summary>
internal static class ProtocolSchemas
{
    private const string SchemaBaseUri = "https://github.com/RectangleEquals/UnityLudometryMCP/protocol/schema/";

    /// <summary>The pinned protocol folder (<c>external/protocol/protocol</c>).</summary>
    public static string Root { get; } = typeof(ProtocolSchemas).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "ProtocolRoot").Value!;

    private static readonly Lazy<SchemaSet> Schemas = new(() => new SchemaSet(Path.Combine(Root, "schema")));

    /// <summary>Validates <paramref name="json"/> against a schema reference such as <c>methods/ping.schema.json#/$defs/result</c>.</summary>
    public static IReadOnlyList<string> Validate(string json, string schemaReference) => Schemas.Value.Validate(json, schemaReference);

    private sealed class SchemaSet
    {
        private readonly string _directory;
        private readonly BuildOptions _build;
        private readonly EvaluationOptions _evaluation = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
        private readonly Lock _lock = new();

        public SchemaSet(string directory)
        {
            _directory = directory;
            var registry = new SchemaRegistry();
            _build = new BuildOptions { SchemaRegistry = registry };
            registry.Fetch = (uri, _) => Load(uri);
        }

        public IReadOnlyList<string> Validate(string json, string schemaReference)
        {
            EvaluationResults results;
            lock (_lock)
            {
                var wrapper = JsonSchema.FromText(
                    $"{{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"$ref\":\"{SchemaBaseUri}{schemaReference}\"}}",
                    _build,
                    new Uri($"{SchemaBaseUri}__validate/{Guid.NewGuid():N}.json"));
                using var document = JsonDocument.Parse(json);
                results = wrapper.Evaluate(document.RootElement, _evaluation);
            }

            if (results.IsValid)
            {
                return Array.Empty<string>();
            }

            return (results.Details ?? [])
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation} ({d.SchemaLocation}): {e.Key}: {e.Value}"))
                .DefaultIfEmpty("invalid (no details)")
                .ToList();
        }

        private JsonSchema? Load(Uri uri)
        {
            var text = uri.GetLeftPart(UriPartial.Path);
            if (!text.StartsWith(SchemaBaseUri, StringComparison.Ordinal))
            {
                return null;
            }

            var path = Path.Combine(_directory, text[SchemaBaseUri.Length..].Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? JsonSchema.FromText(File.ReadAllText(path), _build) : null;
        }
    }
}
