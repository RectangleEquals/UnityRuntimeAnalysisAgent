using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Messages;
using UnityRuntimeAnalysisAgent.Core.Diagnostics;

namespace UnityRuntimeAnalysisAgent.Core.Rules;

/// <summary>
/// What can be decided about a rule from its content alone: whether it is well formed (exactly one kind per condition and
/// action, combinator options where they belong, valid patterns), the mode it needs, and whether it writes files.
/// </summary>
internal static class RuleCheck
{
    /// <summary>Nesting depth of conditions at most.</summary>
    public const int MaxDepth = 16;

    /// <summary>Conditions (leaves and combinators) per rule at most.</summary>
    public const int MaxConditions = 64;

    /// <summary>Actions per rule at most.</summary>
    public const int MaxActions = 64;

    private static readonly string[] ConditionKinds = { "scene", "delay", "value", "hook", "event", "log", "ui", "exception", "prompt", "pick", "predicate", "all", "any", "seq", "not", "count" };

    private static readonly string[] ActionKinds =
        { "pause", "waitFrames", "waitMs", "screenshot", "snapshot", "hits", "logs", "mark", "notify", "highlight", "exec", "uiClick", "invoke", "resume", "stayPaused", "emit" };

    private static readonly HashSet<string> FullActions = new(StringComparer.Ordinal) { "pause", "exec", "uiClick", "invoke", "resume", "stayPaused" };

    /// <summary>Throws <c>INVALID_PARAMS</c> (with the offending path) when the rule is malformed.</summary>
    public static void Validate(Rule rule)
    {
        var count = 0;
        Condition(rule.When, "params.when", 1, ref count);
        if (rule.Then.Count == 0)
        {
            throw ProtocolException.InvalidParams("params.then", "A rule needs at least one action.");
        }

        if (rule.Then.Count > MaxActions)
        {
            throw ProtocolException.InvalidParams("params.then", $"A rule has at most {MaxActions} actions.");
        }

        for (var i = 0; i < rule.Then.Count; i++)
        {
            Action(rule.Then[i], $"params.then[{i}]");
        }

        if (rule.Fire?.Mode is { } mode && mode is not ("once" or "repeat"))
        {
            throw ProtocolException.InvalidParams("params.fire.mode", "params.fire.mode must be once or repeat.");
        }

        if (rule.Fire?.MaxFires is { } maxFires && rule.Fire.Mode != "repeat" && maxFires != 1)
        {
            throw ProtocolException.InvalidParams("params.fire.maxFires", "params.fire.maxFires needs mode repeat (a once rule fires once).");
        }

        if (rule.Id is { } id && (id.Length == 0 || id.Length > 128))
        {
            throw ProtocolException.InvalidParams("params.id", "params.id must be 1 to 128 characters.");
        }

        var writes = WritesFiles(rule);
        if (writes && rule.OutDir is null)
        {
            throw ProtocolException.InvalidParams("params.outDir", "The rule's actions write files (screenshots): give params.outDir.");
        }

        if (rule.OutDir is { } outDir)
        {
            if (!Path.IsPathRooted(outDir))
            {
                throw ProtocolException.InvalidParams("params.outDir", "params.outDir must be an absolute path.");
            }

            if (!Directory.Exists(outDir))
            {
                throw ProtocolException.InvalidParams("params.outDir", $"The directory {outDir} doesn't exist.");
            }

            for (var i = 0; i < rule.Then.Count; i++)
            {
                if (rule.Then[i].Screenshot is { } shot && (shot.Path ?? shot.OutDir) is { } own && !IsUnder(own, outDir))
                {
                    throw ProtocolException.InvalidParams($"params.then[{i}].screenshot", "A rule writes files only under its outDir.");
                }
            }
        }

        if (rule.PauseImmediately == true && !rule.Then.Any(a => a.Pause is not null))
        {
            throw ProtocolException.InvalidParams("params.pauseImmediately", "pauseImmediately needs a pause action.");
        }
    }

    /// <summary>The mode the rule needs: Full when anything in it changes the game or runs code, else ReadOnly.</summary>
    public static AgentMode RequiredMode(Rule rule) =>
        rule.PauseImmediately == true || rule.Then.Any(a => FullActions.Contains(KindOf(a))) || Conditions(rule.When).Any(c => c.Predicate is not null)
            ? AgentMode.Full
            : AgentMode.ReadOnly;

    /// <summary>Whether any action writes files (only screenshots do; snippets write where they're told).</summary>
    public static bool WritesFiles(Rule rule) => rule.Then.Any(a => a.Screenshot is not null);

    /// <summary>The action's kind (its one property).</summary>
    public static string KindOf(RuleAction a) => ActionKindsOf(a).FirstOrDefault() ?? "none";

    /// <summary>The condition's kind (its one kind or combinator).</summary>
    public static string KindOf(RuleCondition c) => ConditionKindsOf(c).FirstOrDefault() ?? "none";

    /// <summary>Every condition in the tree, depth first.</summary>
    public static IEnumerable<RuleCondition> Conditions(RuleCondition root)
    {
        yield return root;
        foreach (var child in (root.All ?? Enumerable.Empty<RuleCondition>()).Concat(root.Any ?? Enumerable.Empty<RuleCondition>()).Concat(root.Seq ?? Enumerable.Empty<RuleCondition>())
                     .Concat(root.Not is null ? Enumerable.Empty<RuleCondition>() : new[] { root.Not }).Concat(root.Count is null ? Enumerable.Empty<RuleCondition>() : new[] { root.Count }))
        {
            foreach (var nested in Conditions(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>A regular expression from a rule (<c>INVALID_PARAMS</c> when it doesn't compile).</summary>
    public static Regex Pattern(string pattern, string param)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException e)
        {
            throw ProtocolException.InvalidParams(param, $"{param} isn't a valid regular expression: {e.Message}");
        }
    }

    private static void Condition(RuleCondition c, string path, int depth, ref int count)
    {
        if (depth > MaxDepth)
        {
            throw ProtocolException.InvalidParams(path, $"Conditions nest at most {MaxDepth} levels deep.");
        }

        if (++count > MaxConditions)
        {
            throw ProtocolException.InvalidParams(path, $"A rule has at most {MaxConditions} conditions.");
        }

        var kinds = ConditionKindsOf(c).ToList();
        if (kinds.Count != 1)
        {
            throw ProtocolException.InvalidParams(path, kinds.Count == 0
                ? $"{path} needs one condition kind or combinator ({string.Join(", ", ConditionKinds)})."
                : $"{path} has {string.Join(" and ", kinds)}: give exactly one condition kind or combinator per object (combine them with all, any or seq).");
        }

        var kind = kinds[0];
        Only(c.Simultaneous is not null, kind == "all", path + ".simultaneous", "simultaneous belongs to all.");
        Only(c.WithinMs is not null, kind == "seq", path + ".withinMs", "withinMs belongs to seq.");
        Only(c.ForMs is not null, kind == "not", path + ".forMs", "forMs belongs to not.");
        Only(c.N is not null, kind == "count", path + ".n", "n belongs to count.");

        switch (kind)
        {
            case "scene":
                var scene = c.Scene!;
                var set = new[] { scene.Loaded, scene.Active, scene.Unloaded }.Count(s => s is not null);
                if (set != 1)
                {
                    throw ProtocolException.InvalidParams(path + ".scene", "A scene condition has exactly one of loaded, active and unloaded.");
                }

                Pattern((scene.Loaded ?? scene.Active ?? scene.Unloaded)!, path + ".scene");
                break;
            case "delay":
                if (new[] { c.Delay!.RealtimeMs, c.Delay.GameTimeMs, c.Delay.Frames }.Count(v => v is not null) != 1)
                {
                    throw ProtocolException.InvalidParams(path + ".delay", "A delay condition has exactly one of realtimeMs, gameTimeMs and frames.");
                }

                break;
            case "value":
                var value = c.Value!;
                if ((value.Op is not null) == (value.Changed == true))
                {
                    throw ProtocolException.InvalidParams(path + ".value", "A value condition has either op (with value) or changed: true.");
                }

                if (value.Op == "regex")
                {
                    Pattern((value.Value as UnityLudometry.Protocol.Json.JsonString)?.Value ?? throw ProtocolException.InvalidParams(path + ".value.value", "regex needs a pattern string."), path + ".value.value");
                }

                break;
            case "hook":
                if (c.Hook!.Phase is { } phase && phase is not ("enter" or "exit" or "throw"))
                {
                    throw ProtocolException.InvalidParams(path + ".hook.phase", "phase must be enter, exit or throw.");
                }

                break;
            case "log":
                Pattern(c.Log!.Regex, path + ".log.regex");
                if (c.Log.MinLevel is { } level && LogBuffer.Rank(level) < 0)
                {
                    throw ProtocolException.InvalidParams(path + ".log.minLevel", "minLevel must be debug, info, warning, error, exception or fatal.");
                }

                break;
            case "exception":
                if (c.Exception!.TypeRegex is { } typeRegex)
                {
                    Pattern(typeRegex, path + ".exception.typeRegex");
                }

                if (c.Exception.MessageRegex is { } messageRegex)
                {
                    Pattern(messageRegex, path + ".exception.messageRegex");
                }

                break;
            case "ui":
                if (c.Ui!.State is not ("appears" or "disappears" or "interactable"))
                {
                    throw ProtocolException.InvalidParams(path + ".ui.state", "state must be appears, disappears or interactable.");
                }

                if (c.Ui.Text is { } text)
                {
                    Pattern(text, path + ".ui.text");
                }

                break;
            case "all" or "any" or "seq":
                var children = c.All ?? c.Any ?? c.Seq!;
                if (children.Count == 0)
                {
                    throw ProtocolException.InvalidParams(path + "." + kind, $"{kind} needs at least one condition.");
                }

                for (var i = 0; i < children.Count; i++)
                {
                    Condition(children[i], $"{path}.{kind}[{i}]", depth + 1, ref count);
                }

                break;
            case "not":
                if (c.ForMs is null)
                {
                    throw ProtocolException.InvalidParams(path + ".forMs", "not needs forMs: the window in which its condition must not occur.");
                }

                Condition(c.Not!, path + ".not", depth + 1, ref count);
                break;
            case "count":
                if (c.N is null)
                {
                    throw ProtocolException.InvalidParams(path + ".n", "count needs n: how many times its condition must occur.");
                }

                Condition(c.Count!, path + ".count", depth + 1, ref count);
                break;
        }
    }

    private static void Action(RuleAction a, string path)
    {
        var kinds = ActionKindsOf(a).ToList();
        if (kinds.Count != 1)
        {
            throw ProtocolException.InvalidParams(path, kinds.Count == 0
                ? $"{path} needs one action ({string.Join(", ", ActionKinds)})."
                : $"{path} has {string.Join(" and ", kinds)}: give exactly one action per object.");
        }

        if (a.Hits?.Since is { } hitsSince && hitsSince is not ("armed" or "lastFire"))
        {
            throw ProtocolException.InvalidParams(path + ".hits.since", "since must be armed or lastFire.");
        }

        if (a.Logs is { } logs)
        {
            if (logs.Since is { } logsSince && logsSince is not ("armed" or "lastFire"))
            {
                throw ProtocolException.InvalidParams(path + ".logs.since", "since must be armed or lastFire.");
            }

            if (logs.MinLevel is { } level && LogBuffer.Rank(level) < 0)
            {
                throw ProtocolException.InvalidParams(path + ".logs.minLevel", "minLevel must be debug, info, warning, error, exception or fatal.");
            }
        }

        if (a.Screenshot is { } shot && shot.Path is not null && shot.OutDir is not null)
        {
            throw ProtocolException.InvalidParams(path + ".screenshot", "Give at most one of path and outDir (without either, the rule's outDir is used).");
        }
    }

    private static void Only(bool present, bool allowed, string path, string message)
    {
        if (present && !allowed)
        {
            throw ProtocolException.InvalidParams(path, message);
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        if (!Path.IsPathRooted(path))
        {
            return false;
        }

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ConditionKindsOf(RuleCondition c) => new (string Kind, bool Present)[]
    {
        ("scene", c.Scene is not null),
        ("delay", c.Delay is not null),
        ("value", c.Value is not null),
        ("hook", c.Hook is not null),
        ("event", c.Event is not null),
        ("log", c.Log is not null),
        ("ui", c.Ui is not null),
        ("exception", c.Exception is not null),
        ("prompt", c.Prompt is not null),
        ("pick", c.Pick is not null),
        ("predicate", c.Predicate is not null),
        ("all", c.All is not null),
        ("any", c.Any is not null),
        ("seq", c.Seq is not null),
        ("not", c.Not is not null),
        ("count", c.Count is not null),
    }.Where(k => k.Present).Select(k => k.Kind);

    private static IEnumerable<string> ActionKindsOf(RuleAction a) => new (string Kind, bool Present)[]
    {
        ("pause", a.Pause is not null),
        ("waitFrames", a.WaitFrames is not null),
        ("waitMs", a.WaitMs is not null),
        ("screenshot", a.Screenshot is not null),
        ("snapshot", a.Snapshot is not null),
        ("hits", a.Hits is not null),
        ("logs", a.Logs is not null),
        ("mark", a.Mark is not null),
        ("notify", a.Notify is not null),
        ("highlight", a.Highlight is not null),
        ("exec", a.Exec is not null),
        ("uiClick", a.UiClick is not null),
        ("invoke", a.Invoke is not null),
        ("resume", a.Resume is not null),
        ("stayPaused", a.StayPaused is not null),
        ("emit", a.Emit is not null),
    }.Where(k => k.Present).Select(k => k.Kind);
}
