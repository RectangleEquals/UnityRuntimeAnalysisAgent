using System;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityRuntimeAnalysisAgent.Core.Instrumentation;

namespace UnityRuntimeAnalysisAgent.Core.Diagnostics;

/// <summary>The only agent-owned method the instrumentation may patch: <c>agent.healthCheck</c> hooks it, calls it and
/// removes the hook, proving the patching path works in this game.</summary>
public static class HealthProbe
{
    private static int s_calls;

    /// <summary>Does nothing observable (not inlined, so its patch runs).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Ping() => Interlocked.Increment(ref s_calls);

    /// <summary>Counts calls.</summary>
    internal sealed class CountingSink : IMethodSink
    {
        private long _hits;

        public long Hits => Interlocked.Read(ref _hits);

        public object? Enter(CallContext call)
        {
            Interlocked.Increment(ref _hits);
            return null;
        }

        public void Exit(CallContext call, object token, object? result, bool hasResult, long elapsedTicks)
        {
        }

        public void Throw(CallContext call, object token, Exception exception, long elapsedTicks)
        {
        }
    }
}
