using System;
using UnityLudometry.Protocol.Messages;

namespace UnityRuntimeAnalysisAgent.Core.Dispatch;

/// <summary>
/// A result that arrives later. A method that has to wait (for a job, for the requests in a batch) returns one instead of
/// blocking a thread, and completes it when the result is ready; the dispatcher answers the request then. Threads are
/// scarce inside a game (the thread pool is shared with it), so nothing in the agent should hold one just to wait.
/// </summary>
public sealed class Deferred
{
    private readonly object _gate = new();
    private bool _done;
    private ProtocolMessage? _result;
    private Exception? _error;
    private Action<object?>? _onResult;
    private Action<Exception>? _onError;

    /// <summary>Whether it has completed.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _done;
            }
        }
    }

    /// <summary>Completes with a result. Returns false if it had already completed (the later outcome is ignored).</summary>
    public bool Complete(ProtocolMessage result) => Finish(result, null);

    /// <summary>Completes with a failure. Returns false if it had already completed.</summary>
    public bool Fail(Exception error) => Finish(null, error);

    internal void Attach(Action<object?> onResult, Action<Exception> onError)
    {
        lock (_gate)
        {
            if (!_done)
            {
                _onResult = onResult;
                _onError = onError;
                return;
            }
        }

        Deliver(onResult, onError);
    }

    private bool Finish(ProtocolMessage? result, Exception? error)
    {
        Action<object?>? onResult;
        Action<Exception>? onError;
        lock (_gate)
        {
            if (_done)
            {
                return false;
            }

            _done = true;
            _result = result;
            _error = error;
            onResult = _onResult;
            onError = _onError;
        }

        if (onResult is not null && onError is not null)
        {
            Deliver(onResult, onError);
        }

        return true;
    }

    private void Deliver(Action<object?> onResult, Action<Exception> onError)
    {
        if (_error is not null)
        {
            onError(_error);
        }
        else
        {
            onResult(_result);
        }
    }
}
