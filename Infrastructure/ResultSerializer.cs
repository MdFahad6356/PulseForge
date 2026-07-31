using System;
using System.Globalization;
using System.Text;
using PulseForge.Models;

namespace PulseForge.Infrastructure;

public static class ResultSerializer
{
    public static string ToJson(StressTestResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        Add(builder, "startedAt", result.StartedAt.ToString("O"), true);
        Add(builder, "finishedAt", result.FinishedAt.ToString("O"), true);
        Add(builder, "kind", result.Kind.ToString(), true);
        Add(builder, "completed", result.Completed, true);
        Add(builder, "stopReason", result.StopReason, true);
        Add(builder, "durationSeconds", result.DurationSeconds, true);
        Add(builder, "targetCpuLoadPercent", result.TargetCpuLoadPercent, true);
        Add(builder, "cpuWorkers", result.CpuWorkers, true);
        Add(builder, "requestedMemoryMegabytes", result.RequestedMemoryMegabytes, true);
        Add(builder, "actualMemoryMegabytes", result.ActualMemoryMegabytes, true);
        Add(builder, "kernelPasses", result.KernelPasses, true);
        Add(builder, "bytesProcessed", result.BytesProcessed, true);
        Add(builder, "approximateMemoryTrafficGigabytes", result.ApproximateMemoryTrafficGigabytes, true);
        Add(builder, "errors", result.Errors, true);
        Add(builder, "averageCpuPercent", result.AverageCpuPercent, true);
        Add(builder, "peakCpuPercent", result.PeakCpuPercent, true);
        Add(builder, "minimumAvailableMemoryMegabytes", result.MinimumAvailableMemoryMegabytes, false);
        builder.AppendLine("}");
        return builder.ToString();
    }

    public static string ToErrorJson(Exception exception)
    {
        return "{\n  \"success\": false,\n  \"error\": \"" + Escape(exception.ToString()) + "\"\n}\n";
    }

    private static void Add(StringBuilder builder, string name, string value, bool comma)
    {
        builder.Append("  \"").Append(name).Append("\": \"").Append(Escape(value)).Append('"');
        builder.AppendLine(comma ? "," : string.Empty);
    }

    private static void Add(StringBuilder builder, string name, bool value, bool comma)
    {
        builder.Append("  \"").Append(name).Append("\": ").Append(value ? "true" : "false");
        builder.AppendLine(comma ? "," : string.Empty);
    }

    private static void Add(StringBuilder builder, string name, long value, bool comma)
    {
        builder.Append("  \"").Append(name).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(comma ? "," : string.Empty);
    }

    private static void Add(StringBuilder builder, string name, int value, bool comma) => Add(builder, name, (long)value, comma);

    private static void Add(StringBuilder builder, string name, double value, bool comma)
    {
        builder.Append("  \"").Append(name).Append("\": ").Append(value.ToString("0.##", CultureInfo.InvariantCulture));
        builder.AppendLine(comma ? "," : string.Empty);
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }
}
