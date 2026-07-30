using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace lyrics_overlay;

/// <summary>
/// Low-overhead runtime sampling for identifying whether memory is managed,
/// native, or caused by allocation churn. Samples are kept in a fixed buffer.
/// </summary>
public static class RuntimeProfiler
{
    private const int MaxSamples = 60;
    private static readonly object Sync = new();
    private static readonly Queue<RuntimeSample> Samples = new();
    private static System.Threading.Timer? _timer;

    public static void Start(TimeSpan? interval = null)
    {
        if (_timer != null)
            return;

        Capture();
        _timer = new System.Threading.Timer(_ => Capture(), null, interval ?? TimeSpan.FromSeconds(10), interval ?? TimeSpan.FromSeconds(10));
        AppLogger.Log("Runtime profiler started (10-second sampling interval)");
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public static void Capture()
    {
        using var process = Process.GetCurrentProcess();
        var memoryInfo = GC.GetGCMemoryInfo();
        var sample = new RuntimeSample(
            DateTimeOffset.Now,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(false),
            memoryInfo.HeapSizeBytes,
            memoryInfo.FragmentedBytes,
            GC.GetTotalAllocatedBytes(false),
            process.HandleCount,
            process.Threads.Count,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));

        lock (Sync)
        {
            Samples.Enqueue(sample);
            while (Samples.Count > MaxSamples)
                Samples.Dequeue();
        }
    }

    public static void LogReport()
    {
        Capture();
        RuntimeSample[] samples;
        lock (Sync)
            samples = Samples.ToArray();

        if (samples.Length == 0)
            return;

        var first = samples[0];
        var latest = samples[^1];
        double elapsedSeconds = Math.Max(1, (latest.Timestamp - first.Timestamp).TotalSeconds);
        double allocationRate = (latest.TotalAllocatedBytes - first.TotalAllocatedBytes) / elapsedSeconds;

        AppLogger.Log(
            "Memory profile | " +
            $"working set={FormatBytes(latest.WorkingSetBytes)}, " +
            $"private={FormatBytes(latest.PrivateBytes)}, " +
            $"managed live={FormatBytes(latest.ManagedLiveBytes)}, " +
            $"GC heap={FormatBytes(latest.HeapBytes)}, " +
            $"fragmented={FormatBytes(latest.FragmentedBytes)}, " +
            $"allocation rate={FormatBytes((long)allocationRate)}/s, " +
            $"growth since sample start: WS {FormatSignedBytes(latest.WorkingSetBytes - first.WorkingSetBytes)}, " +
            $"private {FormatSignedBytes(latest.PrivateBytes - first.PrivateBytes)}, " +
            $"managed {FormatSignedBytes(latest.ManagedLiveBytes - first.ManagedLiveBytes)}, " +
            $"handles={latest.HandleCount}, threads={latest.ThreadCount}, " +
            $"GCs={latest.Generation0Collections}/{latest.Generation1Collections}/{latest.Generation2Collections}");
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:F1} MiB";

    private static string FormatSignedBytes(long bytes) =>
        $"{(bytes >= 0 ? "+" : "")}{bytes / 1024d / 1024d:F1} MiB";

    private sealed record RuntimeSample(
        DateTimeOffset Timestamp,
        long WorkingSetBytes,
        long PrivateBytes,
        long ManagedLiveBytes,
        long HeapBytes,
        long FragmentedBytes,
        long TotalAllocatedBytes,
        int HandleCount,
        int ThreadCount,
        int Generation0Collections,
        int Generation1Collections,
        int Generation2Collections);
}
