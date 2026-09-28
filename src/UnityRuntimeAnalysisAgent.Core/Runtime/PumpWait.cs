using System;

namespace UnityRuntimeAnalysisAgent.Core.Runtime;

/// <summary>
/// What a main-thread routine yields to wait. A routine is an iterator run by the <see cref="MainThreadPump"/>: it yields
/// these to wait across frames, and yields its result (any other object) to finish. Cancellation is checked at every yield.
/// </summary>
public abstract class PumpWait
{
    private PumpWait()
    {
    }

    /// <summary>Resume on the next frame.</summary>
    public static PumpWait NextFrame { get; } = new FramesWait(1);

    /// <summary>Resume at the end of this frame (after rendering).</summary>
    public static PumpWait EndOfFrame { get; } = new EndOfFrameWait();

    /// <summary>Resume after <paramref name="count"/> frames.</summary>
    public static PumpWait Frames(int count) => new FramesWait(Math.Max(1, count));

    /// <summary>Resume once <paramref name="milliseconds"/> of real time have passed.</summary>
    public static PumpWait Realtime(double milliseconds) => new RealtimeWait(Math.Max(0, milliseconds));

    /// <summary>Resume once <paramref name="predicate"/> is true (checked each frame); fail with <c>TIMEOUT</c> after
    /// <paramref name="timeoutMs"/> of real time.</summary>
    public static PumpWait Until(Func<bool> predicate, double timeoutMs) => new UntilWait(predicate ?? throw new ArgumentNullException(nameof(predicate)), timeoutMs);

    internal sealed class FramesWait : PumpWait
    {
        public FramesWait(int count) => Count = count;

        public int Count { get; }
    }

    internal sealed class EndOfFrameWait : PumpWait
    {
    }

    internal sealed class RealtimeWait : PumpWait
    {
        public RealtimeWait(double milliseconds) => Milliseconds = milliseconds;

        public double Milliseconds { get; }
    }

    internal sealed class UntilWait : PumpWait
    {
        public UntilWait(Func<bool> predicate, double timeoutMs)
        {
            Predicate = predicate;
            TimeoutMs = timeoutMs;
        }

        public Func<bool> Predicate { get; }

        public double TimeoutMs { get; }
    }
}
