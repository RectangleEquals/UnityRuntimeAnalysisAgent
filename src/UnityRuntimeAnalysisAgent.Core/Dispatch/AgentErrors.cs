using System;
using System.Reflection;
using UnityLudometry.Protocol;
using UnityLudometry.Protocol.Envelopes;
using UnityLudometry.Protocol.Json;
using UnityRuntimeAnalysisAgent.Core.Hosting;

namespace UnityRuntimeAnalysisAgent.Core.Dispatch;

/// <summary>Thrown by game code the agent invoked (wrapped so it's reported as <c>GAME_EXCEPTION</c>, never as an agent defect).</summary>
public sealed class GameCodeException : Exception
{
    /// <summary>Wraps the game's exception.</summary>
    public GameCodeException(Exception inner)
        : base(inner.Message, inner)
    {
    }
}

/// <summary>
/// The agent's error model. Errors travel as the protocol's <see cref="ProtocolError"/> <c>{code, message, data}</c> with the
/// codes of <see cref="ErrorCodes"/>; handlers throw <see cref="ProtocolException"/> for expected failures. Anything else
/// is mapped here: game exceptions to <c>GAME_EXCEPTION</c>, cancellation to <c>CANCELLED</c>, and every other exception
/// to <c>INTERNAL</c> (the full stack goes to the log, a trimmed one to the client).
/// </summary>
public static class AgentErrors
{
    /// <summary>Stack lines kept in an error's <c>data.stack</c>.</summary>
    public const int StackLines = 8;

    /// <summary>A protocol error.</summary>
    public static ProtocolException Create(string code, string message, JsonObject? data = null) => new(code, message, data);

    /// <summary><c>NOT_FOUND</c>.</summary>
    public static ProtocolException NotFound(string message) => new(ErrorCodes.NotFound, message);

    /// <summary><c>MODE_FORBIDDEN {requiredMode}</c>.</summary>
    public static ProtocolException ModeForbidden(string method, AgentMode required, AgentMode current) => new(
        ErrorCodes.ModeForbidden,
        $"{method} needs mode {AgentModes.ToWire(required)}; the agent is in {AgentModes.ToWire(current)}.",
        Data(("requiredMode", new JsonString(AgentModes.ToWire(required))), ("hint", new JsonString("The user decides the mode; ULM asks them before raising it."))));

    /// <summary>Wraps an exception thrown by invoked game code.</summary>
    public static GameCodeException Game(Exception thrownByGame) => new(Unwrap(thrownByGame));

    /// <summary><c>EXEC_FAILED {phase}</c>: loading (<c>load</c>), binding (<c>bind</c>) or running (<c>run</c>) an assembly the
    /// client sent failed; the exception, if any, is described in <c>data</c>.</summary>
    public static ProtocolException ExecFailed(string phase, string message, Exception? exception = null)
    {
        var data = Data(("phase", new JsonString(phase)));
        if (exception is not null)
        {
            exception = Unwrap(exception);
            data.Add("exceptionType", new JsonString(exception.GetType().FullName ?? exception.GetType().Name));
            data.Add("exceptionMessage", new JsonString(exception.Message));
            data.Add("stack", new JsonString(Trim(exception.StackTrace)));
        }

        return new ProtocolException(ErrorCodes.ExecFailed, message, data);
    }

    /// <summary>Maps any exception to the error the client gets, logging agent defects in full.</summary>
    public static ProtocolError FromException(Exception exception, IAgentLogger log, string what)
    {
        exception = Unwrap(exception);
        switch (exception)
        {
            case ProtocolException protocol:
                return protocol.ToError();
            case OperationCanceledException:
                return new ProtocolError { Code = ErrorCodes.Cancelled, Message = "The request was cancelled." };
            case GameCodeException game:
                var inner = game.InnerException ?? game;
                return new ProtocolError
                {
                    Code = ErrorCodes.GameException,
                    Message = $"The game threw {inner.GetType().Name}: {inner.Message}",
                    Data = Data(("exceptionType", new JsonString(inner.GetType().FullName ?? inner.GetType().Name)), ("stack", new JsonString(Trim(inner.StackTrace)))),
                };
            default:
                log.Error($"{what} failed.", exception);
                return new ProtocolError
                {
                    Code = ErrorCodes.Internal,
                    Message = $"The agent failed ({exception.GetType().Name}): {exception.Message}",
                    Data = Data(("exceptionType", new JsonString(exception.GetType().FullName ?? exception.GetType().Name)), ("stack", new JsonString(Trim(exception.StackTrace)))),
                };
        }
    }

    /// <summary>An error as JSON (<c>{code, message, data?}</c>).</summary>
    public static JsonObject ToJson(ProtocolError error)
    {
        var json = new JsonObject();
        json.Add("code", new JsonString(error.Code));
        json.Add("message", new JsonString(error.Message));
        if (error.Data is not null)
        {
            json.Add("data", error.Data);
        }

        return json;
    }

    /// <summary>Unwraps reflection's <see cref="TargetInvocationException"/>.</summary>
    public static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: { } inner })
        {
            exception = inner;
        }

        return exception;
    }

    internal static JsonObject Data(params (string Key, JsonValue Value)[] entries)
    {
        var data = new JsonObject();
        foreach (var (key, value) in entries)
        {
            data.Add(key, value);
        }

        return data;
    }

    private static string Trim(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return string.Empty;
        }

        var lines = stack!.Split('\n');
        return lines.Length <= StackLines ? stack.TrimEnd() : string.Join("\n", lines, 0, StackLines).TrimEnd() + $"\n   … {lines.Length - StackLines} more";
    }
}
