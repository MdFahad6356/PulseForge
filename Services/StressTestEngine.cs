using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PulseForge.Infrastructure;
using PulseForge.Models;

namespace PulseForge.Services;

public sealed class StressTestEngine
{
    private const int CpuCycleMilliseconds = 100;
    private const int MemoryBlockMegabytes = 16;
    private const long CriticalAvailableMemoryMegabytes = 384;
    private static readonly ulong ExpectedKernelChecksum = ComputeKernel();

    public async Task<StressTestResult> RunAsync(
        StressTestSettings settings,
        IProgress<TestProgress>? progress,
        CancellationToken cancellationToken)
    {
        Validate(settings);

        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        var durationEnded = false;
        var safetyStop = false;
        var safetyReason = string.Empty;
        long kernelPasses = 0;
        long bytesProcessed = 0;
        long errors = 0;
        var actualMemoryMegabytes = CalculateSafeMemoryAllocation(settings);

        using var durationCts = new CancellationTokenSource(settings.Duration);
        using var safetyCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            durationCts.Token,
            safetyCts.Token);

        var token = linkedCts.Token;
        var work = new List<Task>();

        if (settings.Kind is StressTestKind.Cpu or StressTestKind.Combined)
        {
            for (var i = 0; i < settings.CpuWorkers; i++)
            {
                work.Add(Task.Factory.StartNew(
                    () => RunCpuWorker(
                        settings.CpuLoadPercent,
                        () => Interlocked.Increment(ref kernelPasses),
                        () => Interlocked.Increment(ref errors),
                        token),
                    token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default));
            }
        }

        if (settings.Kind is StressTestKind.Memory or StressTestKind.Combined)
        {
            work.Add(Task.Factory.StartNew(
                () => RunMemoryWorker(
                    actualMemoryMegabytes,
                    bytes => Interlocked.Add(ref bytesProcessed, bytes),
                    count => Interlocked.Add(ref errors, count),
                    token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default));
        }

        var monitorTask = MonitorAsync();

        try
        {
            await Task.WhenAll(work);
        }
        catch (OperationCanceledException)
        {
            durationEnded = durationCts.IsCancellationRequested;
        }
        finally
        {
            linkedCts.Cancel();
            try
            {
                await monitorTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when a test finishes or the user presses Stop.
            }
        }

        stopwatch.Stop();
        var wasUserCancelled = cancellationToken.IsCancellationRequested;
        var completed = durationEnded && !wasUserCancelled && !safetyStop;
        var reason = completed
            ? "Completed"
            : safetyStop
                ? safetyReason
                : wasUserCancelled
                    ? "Stopped by user"
                    : "Stopped before completion";

        return new StressTestResult
        {
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now,
            Kind = settings.Kind,
            Completed = completed,
            StopReason = reason,
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
            TargetCpuLoadPercent = settings.CpuLoadPercent,
            CpuWorkers = settings.Kind == StressTestKind.Memory ? 0 : settings.CpuWorkers,
            RequestedMemoryMegabytes = settings.MemoryMegabytes,
            ActualMemoryMegabytes = settings.Kind == StressTestKind.Cpu ? 0 : actualMemoryMegabytes,
            KernelPasses = kernelPasses,
            BytesProcessed = bytesProcessed,
            Errors = errors
        };

        async Task MonitorAsync()
        {
            while (!token.IsCancellationRequested)
            {
                var elapsed = stopwatch.Elapsed;
                var remaining = settings.Duration - elapsed;
                progress?.Report(new TestProgress
                {
                    Elapsed = elapsed,
                    Remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining,
                    KernelPasses = Interlocked.Read(ref kernelPasses),
                    BytesProcessed = Interlocked.Read(ref bytesProcessed),
                    Errors = Interlocked.Read(ref errors),
                    ActualMemoryMegabytes = settings.Kind == StressTestKind.Cpu ? 0 : actualMemoryMegabytes
                });

                var memory = NativeTelemetry.ReadMemory();
                if (memory.AvailableMegabytes > 0 && memory.AvailableMegabytes < CriticalAvailableMemoryMegabytes)
                {
                    safetyStop = true;
                    safetyReason = $"Safety stop: only {memory.AvailableMegabytes:N0} MB memory available";
                    safetyCts.Cancel();
                    break;
                }

                await Task.Delay(500, token);
            }
        }
    }

    private static int CalculateSafeMemoryAllocation(StressTestSettings settings)
    {
        if (settings.Kind == StressTestKind.Cpu)
        {
            return 0;
        }

        var memory = NativeTelemetry.ReadMemory();
        if (memory.AvailableMegabytes <= 0)
        {
            return Clamp(settings.MemoryMegabytes, 64, 1024);
        }

        var headroomLimited = Math.Max(64, memory.AvailableMegabytes - 768);
        var percentageLimited = Math.Max(64, (long)(memory.AvailableMegabytes * 0.45));
        var safeMaximum = (int)Math.Min(2048, Math.Min(headroomLimited, percentageLimited));
        return Clamp(settings.MemoryMegabytes, 64, safeMaximum);
    }

    private static void RunCpuWorker(
        int targetLoadPercent,
        Action passCompleted,
        Action errorDetected,
        CancellationToken token)
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        var busyMilliseconds = Math.Max(1, CpuCycleMilliseconds * targetLoadPercent / 100);

        while (!token.IsCancellationRequested)
        {
            var cycle = Stopwatch.StartNew();
            while (cycle.ElapsedMilliseconds < busyMilliseconds && !token.IsCancellationRequested)
            {
                var checksum = ComputeKernel();
                if (checksum != ExpectedKernelChecksum)
                {
                    errorDetected();
                }

                passCompleted();
            }

            var sleepMilliseconds = CpuCycleMilliseconds - (int)cycle.ElapsedMilliseconds;
            if (sleepMilliseconds > 0 && token.WaitHandle.WaitOne(sleepMilliseconds))
            {
                break;
            }
        }

        token.ThrowIfCancellationRequested();
    }

    private static void RunMemoryWorker(
        int memoryMegabytes,
        Action<long> bytesProcessed,
        Action<long> errorsDetected,
        CancellationToken token)
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        var blocks = AllocateBlocks(memoryMegabytes);
        ulong pattern = 0xA5A5_5A5A_F0F0_0F0FUL;

        while (!token.IsCancellationRequested)
        {
            foreach (var block in blocks)
            {
                token.ThrowIfCancellationRequested();
                for (var i = 0; i < block.Length; i++)
                {
                    block[i] = pattern;
                    if ((i & 0x7FFFF) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }
                bytesProcessed(block.LongLength * sizeof(ulong));
            }

            long errors = 0;
            foreach (var block in blocks)
            {
                for (var i = 0; i < block.Length; i++)
                {
                    if (block[i] != pattern)
                    {
                        errors++;
                    }

                    if ((i & 0x7FFFF) == 0)
                    {
                        token.ThrowIfCancellationRequested();
                    }
                }

                bytesProcessed(block.LongLength * sizeof(ulong));
            }

            if (errors > 0)
            {
                errorsDetected(errors);
            }

            pattern = RotatePattern(pattern);
        }

        token.ThrowIfCancellationRequested();
    }

    private static List<ulong[]> AllocateBlocks(int memoryMegabytes)
    {
        var blocks = new List<ulong[]>();
        var remaining = memoryMegabytes;
        while (remaining > 0)
        {
            var blockMegabytes = Math.Min(MemoryBlockMegabytes, remaining);
            var elementCount = blockMegabytes * 1024 * 1024 / sizeof(ulong);
            blocks.Add(new ulong[elementCount]);
            remaining -= blockMegabytes;
        }

        return blocks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong ComputeKernel()
    {
        ulong value = 0x9E37_79B9_7F4A_7C15UL;
        for (var i = 0; i < 16_384; i++)
        {
            value ^= value >> 12;
            value ^= value << 25;
            value ^= value >> 27;
            value *= 0x2545_F491_4F6C_DD1DUL;
            value += (ulong)i * 0x9E37_79B1UL;
        }

        return value;
    }

    private static ulong RotatePattern(ulong pattern)
    {
        pattern ^= pattern << 13;
        pattern ^= pattern >> 7;
        pattern ^= pattern << 17;
        return pattern == 0 ? 0x3C3C_C3C3_9696_6969UL : pattern;
    }

    private static void Validate(StressTestSettings settings)
    {
        if (settings.CpuLoadPercent < 10 || settings.CpuLoadPercent > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.CpuLoadPercent));
        }

        if (settings.CpuWorkers < 1 || settings.CpuWorkers > Environment.ProcessorCount)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.CpuWorkers));
        }

        if (settings.MemoryMegabytes < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.MemoryMegabytes));
        }

        if (settings.Duration < TimeSpan.FromSeconds(1) || settings.Duration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(settings.Duration));
        }
    }

    private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));
}
