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
    public static PumpWait Until(Func<bool> predicate, double timeoutMs) => Until(predicate, timeoutMs, null);

    /// <summary>As <see cref="Until(Func{bool}, double)"/>, with what a timeout means in words ("Scene 'Main' wasn't loaded").</summary>
    public static PumpWait Until(Func<bool> predicate, double timeoutMs, string? what) =>
        new UntilWait(predicate ?? throw new ArgumentNullException(nameof(predicate)), timeoutMs, what);

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
        public UntilWait(Func<bool> predicate, double timeoutMs, string? what)
        {
            Predicate = predicate;
            TimeoutMs = timeoutMs;
            What = what;
        }

        public Func<bool> Predicate { get; }

        public double TimeoutMs { get; }

        /// <summary>What a timeout means, in words (null: a condition that didn't become true).</summary>
        public string? What { get; }

        /// <summary>The timeout's message.</summary>
        public string TimeoutMessage => What is null
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "The condition didn't become true within {0:0} ms.", TimeoutMs)
            : string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} within {1:0} ms.", What, TimeoutMs);
    }
}
