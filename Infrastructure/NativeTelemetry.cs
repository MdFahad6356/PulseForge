using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PulseForge.Infrastructure;

public sealed class NativeTelemetry : IDisposable
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _hasPreviousCpuSample;
    private PerformanceCounter? _diskUsageCounter;
    private PerformanceCounter? _diskBytesCounter;
    private readonly List<PerformanceCounter> _gpuCounters = new List<PerformanceCounter>();
    private DateTime _lastGpuRefresh = DateTime.MinValue;
    private NetworkInterface? _wifiInterface;
    private long _previousWifiBytes;
    private long _previousWifiTimestamp;
    private bool _hasPreviousWifiSample;

    public NativeTelemetry()
    {
        InitializeDiskCounters();
        RefreshGpuCounters();
        RefreshWifiInterface();
    }

    public TelemetrySnapshot Sample()
    {
        var cpu = ReadCpuUsage();
        var memory = ReadMemory();
        var power = ReadPower();
        var disk = ReadDisk();
        var wifi = ReadWifi();
        var gpu = ReadGpuUsage();

        return new TelemetrySnapshot(
            cpu,
            memory.UsedPercent,
            memory.AvailableMegabytes,
            memory.TotalMegabytes,
            power.IsOnAc,
            power.BatteryPercent,
            disk.UsagePercent,
            disk.MegabytesPerSecond,
            wifi.MegabitsPerSecond,
            wifi.AdapterName,
            wifi.LinkSpeedMegabits,
            gpu);
    }

    public static MemorySnapshot ReadMemory()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
        {
            return new MemorySnapshot(0, 0, 0);
        }

        var totalMb = (long)(status.TotalPhysical / 1024 / 1024);
        var availableMb = (long)(status.AvailablePhysical / 1024 / 1024);
        var usedPercent = totalMb == 0 ? 0 : Clamp((totalMb - availableMb) * 100d / totalMb, 0, 100);
        return new MemorySnapshot(totalMb, availableMb, usedPercent);
    }

    public static string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown processor";
        }
        catch
        {
            return "Unknown processor";
        }
    }

    private double ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);

        if (!_hasPreviousCpuSample)
        {
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            _hasPreviousCpuSample = true;
            return 0;
        }

        var idleDelta = idle - _previousIdle;
        var kernelDelta = kernel - _previousKernel;
        var userDelta = user - _previousUser;
        var total = kernelDelta + userDelta;

        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;

        return total == 0 ? 0 : Clamp((total - idleDelta) * 100d / total, 0, 100);
    }

    private static PowerSnapshot ReadPower()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return new PowerSnapshot(true, null);
        }

        var battery = status.BatteryLifePercent == 255 ? null : (int?)status.BatteryLifePercent;
        return new PowerSnapshot(status.AcLineStatus == 1, battery);
    }

    private void InitializeDiskCounters()
    {
        try
        {
            _diskUsageCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
            _diskBytesCounter = new PerformanceCounter("PhysicalDisk", "Disk Bytes/sec", "_Total", true);
            _diskUsageCounter.NextValue();
            _diskBytesCounter.NextValue();
        }
        catch
        {
            _diskUsageCounter?.Dispose();
            _diskBytesCounter?.Dispose();
            _diskUsageCounter = null;
            _diskBytesCounter = null;
        }
    }

    private DiskSnapshot ReadDisk()
    {
        try
        {
            var usage = _diskUsageCounter?.NextValue() ?? 0;
            var bytes = _diskBytesCounter?.NextValue() ?? 0;
            return new DiskSnapshot(Clamp(usage, 0, 100), Math.Max(0, bytes / 1024d / 1024d));
        }
        catch
        {
            return new DiskSnapshot(0, 0);
        }
    }

    private double ReadGpuUsage()
    {
        if ((DateTime.UtcNow - _lastGpuRefresh).TotalSeconds >= 12)
        {
            RefreshGpuCounters();
        }

        double maximum = 0;
        var shouldRefresh = false;
        foreach (var counter in _gpuCounters)
        {
            try
            {
                maximum = Math.Max(maximum, counter.NextValue());
            }
            catch
            {
                shouldRefresh = true;
            }
        }

        if (shouldRefresh)
        {
            _lastGpuRefresh = DateTime.MinValue;
        }

        return Clamp(maximum, 0, 100);
    }

    private void RefreshGpuCounters()
    {
        foreach (var counter in _gpuCounters)
        {
            counter.Dispose();
        }

        _gpuCounters.Clear();
        _lastGpuRefresh = DateTime.UtcNow;

        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in category.GetInstanceNames()
                         .Where(name => name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                try
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                    counter.NextValue();
                    _gpuCounters.Add(counter);
                }
                catch
                {
                    // GPU process instances are transient; skip any that disappear during enumeration.
                }
            }
        }
        catch
        {
            // GPU counters are unavailable on some drivers and older Windows builds.
        }
    }

    private WifiSnapshot ReadWifi()
    {
        if (_wifiInterface == null || _wifiInterface.OperationalStatus != OperationalStatus.Up)
        {
            RefreshWifiInterface();
        }

        if (_wifiInterface == null)
        {
            _hasPreviousWifiSample = false;
            return new WifiSnapshot(0, "No active Wi-Fi", 0);
        }

        try
        {
            var statistics = _wifiInterface.GetIPv4Statistics();
            var totalBytes = statistics.BytesReceived + statistics.BytesSent;
            var timestamp = Stopwatch.GetTimestamp();
            double rate = 0;

            if (_hasPreviousWifiSample && timestamp > _previousWifiTimestamp && totalBytes >= _previousWifiBytes)
            {
                var seconds = (timestamp - _previousWifiTimestamp) / (double)Stopwatch.Frequency;
                rate = (totalBytes - _previousWifiBytes) * 8d / seconds / 1_000_000d;
            }

            _previousWifiBytes = totalBytes;
            _previousWifiTimestamp = timestamp;
            _hasPreviousWifiSample = true;

            return new WifiSnapshot(
                Math.Max(0, rate),
                _wifiInterface.Name,
                Math.Max(0, _wifiInterface.Speed / 1_000_000d));
        }
        catch
        {
            RefreshWifiInterface();
            return new WifiSnapshot(0, _wifiInterface?.Name ?? "No active Wi-Fi", 0);
        }
    }

    private void RefreshWifiInterface()
    {
        try
        {
            _wifiInterface = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .OrderByDescending(adapter => adapter.Speed)
                .FirstOrDefault();
        }
        catch
        {
            _wifiInterface = null;
        }

        _hasPreviousWifiSample = false;
    }

    public void Dispose()
    {
        _diskUsageCounter?.Dispose();
        _diskBytesCounter?.Dispose();
        foreach (var counter in _gpuCounters)
        {
            counter.Dispose();
        }

        _gpuCounters.Clear();
    }

    private static ulong ToUInt64(FileTime time) => ((ulong)time.High << 32) | time.Low;

    private static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }
}

public sealed class TelemetrySnapshot
{
    public TelemetrySnapshot(
        double cpuPercent,
        double memoryPercent,
        long availableMemoryMegabytes,
        long totalMemoryMegabytes,
        bool isOnAcPower,
        int? batteryPercent,
        double diskPercent,
        double diskMegabytesPerSecond,
        double wifiMegabitsPerSecond,
        string wifiAdapterName,
        double wifiLinkSpeedMegabits,
        double gpuPercent)
    {
        CpuPercent = cpuPercent;
        MemoryPercent = memoryPercent;
        AvailableMemoryMegabytes = availableMemoryMegabytes;
        TotalMemoryMegabytes = totalMemoryMegabytes;
        IsOnAcPower = isOnAcPower;
        BatteryPercent = batteryPercent;
        DiskPercent = diskPercent;
        DiskMegabytesPerSecond = diskMegabytesPerSecond;
        WifiMegabitsPerSecond = wifiMegabitsPerSecond;
        WifiAdapterName = wifiAdapterName;
        WifiLinkSpeedMegabits = wifiLinkSpeedMegabits;
        GpuPercent = gpuPercent;
    }

    public double CpuPercent { get; }
    public double MemoryPercent { get; }
    public long AvailableMemoryMegabytes { get; }
    public long TotalMemoryMegabytes { get; }
    public bool IsOnAcPower { get; }
    public int? BatteryPercent { get; }
    public double DiskPercent { get; }
    public double DiskMegabytesPerSecond { get; }
    public double WifiMegabitsPerSecond { get; }
    public string WifiAdapterName { get; }
    public double WifiLinkSpeedMegabits { get; }
    public double GpuPercent { get; }
}

public sealed class DiskSnapshot
{
    public DiskSnapshot(double usagePercent, double megabytesPerSecond)
    {
        UsagePercent = usagePercent;
        MegabytesPerSecond = megabytesPerSecond;
    }

    public double UsagePercent { get; }
    public double MegabytesPerSecond { get; }
}

public sealed class WifiSnapshot
{
    public WifiSnapshot(double megabitsPerSecond, string adapterName, double linkSpeedMegabits)
    {
        MegabitsPerSecond = megabitsPerSecond;
        AdapterName = adapterName;
        LinkSpeedMegabits = linkSpeedMegabits;
    }

    public double MegabitsPerSecond { get; }
    public string AdapterName { get; }
    public double LinkSpeedMegabits { get; }
}

public sealed class MemorySnapshot
{
    public MemorySnapshot(long totalMegabytes, long availableMegabytes, double usedPercent)
    {
        TotalMegabytes = totalMegabytes;
        AvailableMegabytes = availableMegabytes;
        UsedPercent = usedPercent;
    }

    public long TotalMegabytes { get; }
    public long AvailableMegabytes { get; }
    public double UsedPercent { get; }
}

public sealed class PowerSnapshot
{
    public PowerSnapshot(bool isOnAc, int? batteryPercent)
    {
        IsOnAc = isOnAc;
        BatteryPercent = batteryPercent;
    }

    public bool IsOnAc { get; }
    public int? BatteryPercent { get; }
}
