using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PulseForge.Infrastructure;
using PulseForge.Models;
using PulseForge.Services;

namespace PulseForge;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0].Equals("--render-ui", StringComparison.OrdinalIgnoreCase))
        {
            var outputPath = Path.GetFullPath(e.Args[1]);
            var previewWindow = new MainWindow();
            MainWindow = previewWindow;
            previewWindow.Show();
            await Task.Delay(1500);
            previewWindow.UpdateLayout();

            var width = Math.Max(1, (int)Math.Ceiling(previewWindow.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(previewWindow.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(previewWindow);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            using (var stream = File.Create(outputPath))
            {
                encoder.Save(stream);
            }

            previewWindow.Hide();
            Environment.Exit(0);
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--smoke-test", StringComparison.OrdinalIgnoreCase))
        {
            var outputPath = Path.GetFullPath(e.Args[1]);
            try
            {
                var engine = new StressTestEngine();
                var result = await engine.RunAsync(
                    new StressTestSettings
                    {
                        Kind = StressTestKind.Combined,
                        Duration = TimeSpan.FromSeconds(3),
                        CpuLoadPercent = 25,
                        CpuWorkers = Math.Min(2, Environment.ProcessorCount),
                        MemoryMegabytes = 64
                    },
                    progress: null,
                    CancellationToken.None);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                File.WriteAllText(outputPath, ResultSerializer.ToJson(result));
                Environment.Exit(result.Errors == 0 && result.Completed ? 0 : 2);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
                File.WriteAllText(outputPath, ResultSerializer.ToErrorJson(ex));
                Environment.Exit(1);
            }

            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
