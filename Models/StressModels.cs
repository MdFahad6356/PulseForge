using System;

namespace PulseForge.Models;

public enum StressTestKind
{
    Cpu,
    Memory,
    Combined
}

public sealed class StressTestSettings
{
    public StressTestKind Kind { get; set; } = StressTestKind.Combined;
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(30);
    public int CpuLoadPercent { get; set; } = 70;
    public int CpuWorkers { get; set; } = Environment.ProcessorCount;
    public int MemoryMegabytes { get; set; } = 512;
}

public sealed class TestProgress
{
    public TimeSpan Elapsed { get; set; }
    public TimeSpan Remaining { get; set; }
    public long KernelPasses { get; set; }
    public long BytesProcessed { get; set; }
    public long Errors { get; set; }
    public int ActualMemoryMegabytes { get; set; }
}

public sealed class StressTestResult
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public StressTestKind Kind { get; set; }
    public bool Completed { get; set; }
    public string StopReason { get; set; } = "Completed";
    public double DurationSeconds { get; set; }
    public int TargetCpuLoadPercent { get; set; }
    public int CpuWorkers { get; set; }
    public int RequestedMemoryMegabytes { get; set; }
    public int ActualMemoryMegabytes { get; set; }
    public long KernelPasses { get; set; }
    public long BytesProcessed { get; set; }
    public double ApproximateMemoryTrafficGigabytes => Math.Round(BytesProcessed / 1024d / 1024d / 1024d, 2);
    public long Errors { get; set; }
    public double AverageCpuPercent { get; set; }
    public double PeakCpuPercent { get; set; }
    public long MinimumAvailableMemoryMegabytes { get; set; }
}

public sealed class ActivityEntry
{
    public string Time { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
