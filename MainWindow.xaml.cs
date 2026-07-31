using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PulseForge.Infrastructure;
using PulseForge.Models;
using PulseForge.Services;

namespace PulseForge;

public partial class MainWindow : Window
{
    private readonly NativeTelemetry _telemetry = new();
    private readonly StressTestEngine _engine = new();
    private readonly DispatcherTimer _telemetryTimer;
    private readonly ObservableCollection<ActivityEntry> _activity = new ObservableCollection<ActivityEntry>();
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _memoryHistory = new();
    private readonly List<StressTestResult> _results = new List<StressTestResult>();
    private readonly List<double> _activeCpuSamples = new List<double>();
    private CancellationTokenSource? _testCancellation;
    private StressTestSettings? _activeSettings;
    private StressTestResult? _lastResult;
    private long _minimumAvailableMemory;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();

        _telemetryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _telemetryTimer.Tick += (_, _) => UpdateTelemetry();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        ActivityList.ItemsSource = _activity;

        ConfigureSystemProfile();
        ConfigureControls();
        SeedChart();
        AddActivity("READY", "Safety controls initialized. No workload is running.");
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _telemetry.Sample();
        _telemetryTimer.Start();
        UpdateTelemetry();
    }

    private void ConfigureSystemProfile()
    {
        var memory = NativeTelemetry.ReadMemory();
        CpuNameText.Text = NativeTelemetry.GetCpuName();
        SystemDetailText.Text = $"{Environment.ProcessorCount} logical processors  ·  {memory.TotalMegabytes / 1024d:0.#} GB RAM\n{Environment.OSVersion.VersionString}";
    }

    private void ConfigureControls()
    {
        WorkerSlider.Maximum = Math.Max(1, Environment.ProcessorCount);
        WorkerSlider.Value = Environment.ProcessorCount;

        var available = NativeTelemetry.ReadMemory().AvailableMegabytes;
        var safeSliderMaximum = Clamp((int)(available * 0.45), 512, 2048);
        MemorySlider.Maximum = safeSliderMaximum;
        MemorySlider.Value = Math.Min(512, safeSliderMaximum);

        UpdateSliderLabels();
        UpdateModeControls();
    }

    private void SeedChart()
    {
        for (var i = 0; i < 60; i++)
        {
            _cpuHistory.Enqueue(0);
            _memoryHistory.Enqueue(0);
        }
    }

    private void UpdateTelemetry()
    {
        var snapshot = _telemetry.Sample();
        CpuUsageText.Text = $"{snapshot.CpuPercent:0}%";
        CpuUsageBar.Value = snapshot.CpuPercent;
        MemoryUsageText.Text = $"{snapshot.MemoryPercent:0}%";
        MemoryDetailText.Text = $"{snapshot.AvailableMemoryMegabytes / 1024d:0.0} GB available";
        DiskUsageText.Text = $"{snapshot.DiskPercent:0}%";
        DiskDetailText.Text = snapshot.DiskMegabytesPerSecond >= 1024
            ? $"{snapshot.DiskMegabytesPerSecond / 1024d:0.0} GB/s"
            : $"{snapshot.DiskMegabytesPerSecond:0.0} MB/s";
        GpuUsageText.Text = $"{snapshot.GpuPercent:0}%";

        if (snapshot.WifiAdapterName == "No active Wi-Fi")
        {
            WifiRateText.Text = "OFF";
            WifiDetailText.Text = "No active adapter";
            WifiDetailText.ToolTip = null;
        }
        else
        {
            WifiRateText.Text = snapshot.WifiMegabitsPerSecond < 100
                ? $"{snapshot.WifiMegabitsPerSecond:0.0}"
                : $"{snapshot.WifiMegabitsPerSecond:0}";
            WifiDetailText.Text = $"Mbps · {Shorten(snapshot.WifiAdapterName, 12)}";
            WifiDetailText.ToolTip = $"{snapshot.WifiAdapterName} · {snapshot.WifiLinkSpeedMegabits:0} Mbps link";
        }

        if (snapshot.IsOnAcPower)
        {
            PowerText.Text = "AC";
            PowerDetailText.Text = snapshot.BatteryPercent is null
                ? "External power"
                : $"External power · {snapshot.BatteryPercent}%";
        }
        else
        {
            PowerText.Text = "BAT";
            PowerDetailText.Text = snapshot.BatteryPercent is null
                ? "Battery power"
                : $"Battery · {snapshot.BatteryPercent}%";
        }

        EnqueueSample(_cpuHistory, snapshot.CpuPercent);
        EnqueueSample(_memoryHistory, snapshot.MemoryPercent);
        UpdateChart();

        if (_isRunning)
        {
            _activeCpuSamples.Add(snapshot.CpuPercent);
            if (_minimumAvailableMemory == 0 || snapshot.AvailableMemoryMegabytes < _minimumAvailableMemory)
            {
                _minimumAvailableMemory = snapshot.AvailableMemoryMegabytes;
            }
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        _activeSettings = BuildSettings();
        _testCancellation = new CancellationTokenSource();
        _activeCpuSamples.Clear();
        _minimumAvailableMemory = 0;
        SetRunningState(true);
        AddActivity("START", $"{_activeSettings.Kind} · {_activeSettings.Duration.TotalSeconds:0}s · {_activeSettings.CpuLoadPercent}% CPU · {_activeSettings.MemoryMegabytes} MB memory");

        var progress = new Progress<TestProgress>(UpdateTestProgress);
        try
        {
            var result = await _engine.RunAsync(_activeSettings, progress, _testCancellation.Token);
            ApplyTelemetrySummary(result);
            _results.Add(result);
            _lastResult = result;
            ShowResult(result);
        }
        catch (Exception ex)
        {
            AddActivity("FAULT", ex.Message);
            LastResultText.Text = "The test could not complete";
            LastResultDetailText.Text = ex.Message;
        }
        finally
        {
            SetRunningState(false);
            _testCancellation.Dispose();
            _testCancellation = null;
            _activeSettings = null;
        }
    }

    private StressTestSettings BuildSettings()
    {
        var kind = CpuMode.IsChecked == true
            ? StressTestKind.Cpu
            : MemoryMode.IsChecked == true
                ? StressTestKind.Memory
                : StressTestKind.Combined;

        var durationSeconds = new[] { Duration30, Duration60, Duration300, Duration900 }
            .Where(button => button.IsChecked == true)
            .Select(button => int.Parse(button.Tag.ToString()!))
            .DefaultIfEmpty(30)
            .First();

        return new StressTestSettings
        {
            Kind = kind,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            CpuLoadPercent = (int)CpuTargetSlider.Value,
            CpuWorkers = (int)WorkerSlider.Value,
            MemoryMegabytes = (int)MemorySlider.Value
        };
    }

    private void UpdateTestProgress(TestProgress progress)
    {
        if (_activeSettings is null)
        {
            return;
        }

        SessionTimeText.Text = progress.Elapsed.ToString(@"mm\:ss");
        SessionDetailText.Text = $"{progress.Remaining.ToString(@"mm\:ss")} remaining";
        var percent = _activeSettings.Duration.TotalMilliseconds <= 0
            ? 0
            : progress.Elapsed.TotalMilliseconds / _activeSettings.Duration.TotalMilliseconds * 100;
        TestProgressBar.Value = Clamp(percent, 0, 100);

        var traffic = progress.BytesProcessed / 1024d / 1024d / 1024d;
        WorkMetricText.Text = _activeSettings.Kind switch
        {
            StressTestKind.Cpu => $"{progress.KernelPasses:N0} verified kernels",
            StressTestKind.Memory => $"{traffic:0.0} GB verified traffic",
            _ => $"{progress.KernelPasses:N0} kernels · {traffic:0.0} GB traffic"
        };
        ErrorMetricText.Text = $"{progress.Errors:N0} errors";
        ErrorMetricText.Foreground = progress.Errors == 0
            ? new SolidColorBrush(Color.FromRgb(163, 230, 53))
            : new SolidColorBrush(Color.FromRgb(251, 113, 133));
    }

    private void ApplyTelemetrySummary(StressTestResult result)
    {
        result.AverageCpuPercent = _activeCpuSamples.Count == 0 ? 0 : Math.Round(_activeCpuSamples.Average(), 1);
        result.PeakCpuPercent = _activeCpuSamples.Count == 0 ? 0 : Math.Round(_activeCpuSamples.Max(), 1);
        result.MinimumAvailableMemoryMegabytes = _minimumAvailableMemory;
    }

    private void ShowResult(StressTestResult result)
    {
        var clean = result.Errors == 0;
        var status = result.Completed && clean ? "CLEAN PASS" : result.Completed ? "ERRORS DETECTED" : "RUN STOPPED";
        LastResultText.Text = $"{status} · {result.Kind}";
        LastResultDetailText.Text = $"{result.DurationSeconds:0.0}s · avg CPU {result.AverageCpuPercent:0.0}% · peak {result.PeakCpuPercent:0.0}% · {result.Errors:N0} errors";
        AddActivity(
            result.Completed && clean ? "PASS" : result.Errors > 0 ? "ERROR" : "STOP",
            $"{result.Kind}: {result.StopReason}; {result.Errors:N0} verification errors; peak CPU {result.PeakCpuPercent:0.0}%.");
    }

    private void SetRunningState(bool running)
    {
        _isRunning = running;
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        CpuMode.IsEnabled = !running;
        MemoryMode.IsEnabled = !running;
        CombinedMode.IsEnabled = !running;
        Duration30.IsEnabled = !running;
        Duration60.IsEnabled = !running;
        Duration300.IsEnabled = !running;
        Duration900.IsEnabled = !running;
        CpuTargetSlider.IsEnabled = !running && MemoryMode.IsChecked != true;
        WorkerSlider.IsEnabled = !running && MemoryMode.IsChecked != true;
        MemorySlider.IsEnabled = !running && CpuMode.IsChecked != true;

        HeaderStatusText.Text = running ? "LOAD ACTIVE" : "SYSTEM IDLE";
        HeaderStatusText.Foreground = new SolidColorBrush(running ? Color.FromRgb(255, 226, 184) : Color.FromRgb(205, 239, 162));
        HeaderStatusDot.Fill = new SolidColorBrush(running ? Color.FromRgb(253, 186, 116) : Color.FromRgb(163, 230, 53));
        SessionDot.Fill = HeaderStatusDot.Fill;

        if (!running)
        {
            SessionDetailText.Text = "Ready to begin";
            TestProgressBar.Value = 0;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_testCancellation is { IsCancellationRequested: false })
        {
            AddActivity("STOP", "Manual stop requested. Waiting for workers to exit safely.");
            _testCancellation.Cancel();
            StopButton.IsEnabled = false;
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e) => UpdateModeControls();

    private void UpdateModeControls()
    {
        if (CpuTargetSlider is null || MemorySlider is null || WorkerSlider is null)
        {
            return;
        }

        var cpuEnabled = MemoryMode?.IsChecked != true;
        var memoryEnabled = CpuMode?.IsChecked != true;
        CpuTargetSlider.IsEnabled = !_isRunning && cpuEnabled;
        WorkerSlider.IsEnabled = !_isRunning && cpuEnabled;
        MemorySlider.IsEnabled = !_isRunning && memoryEnabled;
        CpuTargetSlider.Opacity = cpuEnabled ? 1 : 0.32;
        WorkerSlider.Opacity = cpuEnabled ? 1 : 0.32;
        MemorySlider.Opacity = memoryEnabled ? 1 : 0.32;
    }

    private void CpuTargetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();
    private void MemorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();
    private void WorkerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateSliderLabels();

    private void UpdateSliderLabels()
    {
        if (CpuTargetValue is null || MemoryTargetValue is null || WorkerValue is null)
        {
            return;
        }

        CpuTargetValue.Text = $"{CpuTargetSlider.Value:0}%";
        MemoryTargetValue.Text = $"{MemorySlider.Value:0} MB";
        WorkerValue.Text = $"{WorkerSlider.Value:0} / {Environment.ProcessorCount}";
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null)
        {
            AddActivity("INFO", "Complete a test before exporting a result.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export PulseForge result",
            FileName = $"PulseForge-{_lastResult.Kind}-{_lastResult.StartedAt:yyyyMMdd-HHmmss}.json",
            DefaultExt = ".json",
            Filter = "JSON result (*.json)|*.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, ResultSerializer.ToJson(_lastResult));
        AddActivity("EXPORT", $"Result saved to {Path.GetFileName(dialog.FileName)}.");
    }

    private void AddActivity(string level, string message)
    {
        _activity.Insert(0, new ActivityEntry
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Level = level,
            Message = message
        });

        while (_activity.Count > 50)
        {
            _activity.RemoveAt(_activity.Count - 1);
        }

        EventCountText.Text = $"{_activity.Count} {(_activity.Count == 1 ? "EVENT" : "EVENTS")}";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _activity.Clear();
        EventCountText.Text = "0 EVENTS";
    }

    private static void EnqueueSample(Queue<double> queue, double value)
    {
        queue.Enqueue(Clamp(value, 0, 100));
        while (queue.Count > 60)
        {
            queue.Dequeue();
        }
    }

    private void UpdateChart()
    {
        UpdatePolyline(CpuLine, _cpuHistory);
        UpdatePolyline(MemoryLine, _memoryHistory);
    }

    private void UpdatePolyline(System.Windows.Shapes.Polyline line, IEnumerable<double> samples)
    {
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var values = samples.ToArray();
        var points = new PointCollection(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var x = values.Length <= 1 ? 0 : i * width / (values.Length - 1);
            var y = height - values[i] / 100d * height;
            points.Add(new Point(x, y));
        }

        line.Points = points;
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateChart();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _telemetryTimer.Stop();
        _testCancellation?.Cancel();
        _telemetry.Dispose();
        Application.Current.Shutdown();
    }

    private static string Shorten(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : value.Substring(0, maximumLength - 1) + "…";
    }

    private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));
    private static double Clamp(double value, double minimum, double maximum) => Math.Max(minimum, Math.Min(maximum, value));
}
