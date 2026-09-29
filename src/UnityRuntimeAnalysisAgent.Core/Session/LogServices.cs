using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Json;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Diagnostics;
using UnityRuntimeAnalysisAgent.Core.Dispatch;
using UnityRuntimeAnalysisAgent.Core.Runtime;

namespace UnityRuntimeAnalysisAgent.Core.Session;

/// <summary>The unified log: <c>logs.tail</c>, <c>logs.search</c>, <c>logs.mark</c>, and the <c>log</c> event.</summary>
internal sealed class LogServices
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 10_000;

    private readonly LogBuffer _logs;

    public LogServices(LogBuffer logs, EventHub events)
    {
        _logs = logs;
        events.RegisterFilter(EventKinds.Log, Deliver);
        logs.Added += entry =>
        {
            if (events.HasSubscribers(EventKinds.Log))
            {
                events.PublishItem(EventKinds.Log, entry.ToJson());
            }
        };
    }

    [RpcMethod(Methods.LogsTail)]
    public ProtocolMessage Tail(RequestContext context, LogsTailParams p)
    {
        var minRank = p.MinLevel is null ? 0 : LogBuffer.Rank(p.MinLevel) is var rank and >= 0 ? rank
            : throw ProtocolException.InvalidParams("params.minLevel", "params.minLevel must be debug, info, warning, error, exception or fatal.");
        var (items, next, dropped) = _logs.Tail(p.SinceSeq ?? 0, Limit(p.Limit), minRank, p.Sources, p.Channels, p.Regex is null ? null : Pattern(p.Regex, "params.regex"));
        return new LogsTailResult { Items = items, NextSeq = next, Dropped = dropped };
    }

    [RpcMethod(Methods.LogsSearch)]
    public ProtocolMessage Search(RequestContext context, LogsSearchParams p) => new LogsSearchResult
    {
        Items = _logs.Search(Pattern(p.Regex, "params.regex"), p.FromSeq ?? 0, p.ToSeq ?? long.MaxValue, Limit(p.Limit)),
    };

    [RpcMethod(Methods.LogsMark)]
    public ProtocolMessage Mark(RequestContext context, LogsMarkParams p) => new LogsMarkResult { Seq = _logs.Mark(p.Label) };

    // A subscriber's filter: {minLevel?, sources?, channels?}.
    private static bool Deliver(JsonObject filter, JsonValue item)
    {
        var entry = LogEntry.Read(item, "item");
        var minRank = filter["minLevel"] is JsonString { Value: var level } ? Math.Max(0, LogBuffer.Rank(level)) : 0;
        return LogBuffer.Matches(entry, minRank, Strings(filter["sources"]), Strings(filter["channels"]), null);
    }

    private static List<string>? Strings(JsonValue? value) => value is JsonArray array ? array.OfType<JsonString>().Select(s => s.Value).ToList() : null;

    private static int Limit(long? limit) => (int)Math.Max(1, Math.Min(limit ?? DefaultLimit, MaxLimit));

    private static Regex Pattern(string pattern, string param)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException e)
        {
            throw ProtocolException.InvalidParams(param, $"{param} isn't a valid regular expression: {e.Message}");
        }
    }
}
