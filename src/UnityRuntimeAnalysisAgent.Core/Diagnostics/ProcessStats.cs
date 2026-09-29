using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UnityRuntimeAnalysisAgent.Core.Diagnostics;

/// <summary>
/// The process's working set, private bytes and thread count. Unity's Mono returns 0 for these through
/// <see cref="Process"/>, so on Windows they are read from the OS directly (psapi, toolhelp); elsewhere, or if that
/// fails, <see cref="Process"/>'s values are used.
/// </summary>
public static class ProcessStats
{
    private const uint Th32csSnapThread = 0x00000004;

    /// <summary>How long a reading is reused (the thread count walks every thread on the system).</summary>
    public const int CacheMs = 250;

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static (long WorkingSet, long PrivateBytes, long Threads) s_last;
    private static long s_lastAtMs = -CacheMs;

    /// <summary>(working set, private bytes, threads); 0 for anything that can't be read. Readings are reused for
    /// <see cref="CacheMs"/>.</summary>
    public static (long WorkingSet, long PrivateBytes, long Threads) Read()
    {
        lock (Gate)
        {
            var now = Clock.ElapsedMilliseconds;
            if (now - s_lastAtMs < CacheMs)
            {
                return s_last;
            }

            s_last = ReadNow();
            s_lastAtMs = Clock.ElapsedMilliseconds;
            return s_last;
        }
    }

    private static (long WorkingSet, long PrivateBytes, long Threads) ReadNow()
    {
        long workingSet = 0, privateBytes = 0, threads = 0;
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            try
            {
                var counters = new ProcessMemoryCountersEx { Cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx)) };
                if (GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Cb))
                {
                    workingSet = (long)counters.WorkingSetSize;
                    privateBytes = (long)counters.PrivateUsage;
                }

                threads = CountThreads(GetCurrentProcessId());
            }
            catch (Exception)
            {
                // Not available here: fall back below.
            }
        }

        if (workingSet == 0 || privateBytes == 0 || threads == 0)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                workingSet = workingSet != 0 ? workingSet : Safe(() => process.WorkingSet64);
                privateBytes = privateBytes != 0 ? privateBytes : Safe(() => process.PrivateMemorySize64);
                threads = threads != 0 ? threads : Safe(() => process.Threads.Count);
            }
            catch (Exception)
            {
                // Nothing more to try.
            }
        }

        return (workingSet, privateBytes, threads);
    }

    private static long CountThreads(uint processId)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapThread, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return 0;
        }

        try
        {
            var entry = new ThreadEntry32 { Size = (uint)Marshal.SizeOf(typeof(ThreadEntry32)) };
            long count = 0;
            for (var more = Thread32First(snapshot, ref entry); more; more = Thread32Next(snapshot, ref entry))
            {
                if (entry.OwnerProcessId == processId)
                {
                    count++;
                }
            }

            return count;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static long Safe(Func<long> read)
    {
        try
        {
            return Math.Max(0, read());
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint Cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
