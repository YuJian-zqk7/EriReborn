using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;

namespace EriReborn.Desktop.Browser;

/// <summary>
/// Diagnostic entry point: <c>EriReborn.exe --render &lt;url&gt;</c> loads a page in the embedded
/// browser and prints what it found. The share pages that matter here (蓝奏's JavaScript challenge,
/// 迅雷's captcha-minted page) can only be judged against a real render, and this runs the very same
/// <see cref="WebViewHost"/> the cloud providers borrow — no clicking, no guessing.
/// </summary>
internal sealed class RenderProbe : Application
{
    private static string _url = string.Empty;
    private static int _exitCode;

    public static int Run(string url)
    {
        _url = url;
        AppBuilder.Configure<RenderProbe>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
        return _exitCode;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var host = new WebViewHost();
        var window = new Window
        {
            Width = 1100,
            Height = 820,
            Position = new PixelPoint(-3600, -3600),
            ShowInTaskbar = false,
            Title = "EriReborn · render probe",
            Content = host,
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = window;
        }

        Console.WriteLine("probe.start url=" + _url);
        Console.Out.Flush();

        try
        {
            window.Show();
            Console.WriteLine("probe.shown visible=" + window.IsVisible + " hasRoot=" + (host.GetVisualRoot() is not null));
        }
        catch (Exception ex)
        {
            Console.WriteLine("probe.show.failed=" + ex.GetType().Name + ": " + ex.Message);
        }

        // A diagnostic must never hang: report and leave after a hard deadline either way.
        _ = Task.Delay(TimeSpan.FromSeconds(45)).ContinueWith(_ =>
        {
            Console.WriteLine("probe.timeout");
            Console.Out.Flush();
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
            {
                life.Shutdown(3);
            }
        });

        _ = ProbeAsync(host);
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ProbeAsync(WebViewHost host)
    {
        try
        {
            var html = await host.RenderAsync(new Uri(_url));
            Console.WriteLine("render.url=" + _url);
            Console.WriteLine("render.length=" + (html?.Length ?? -1));

            foreach (var marker in new[] { "filemoreajax", "ifr2", "file_down", "sign", "pwd", "off{", "bakstotre", "ajaxm" })
            {
                Console.WriteLine($"marker[{marker}]={(html?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false)}");
            }

            var dump = Path.Combine(Path.GetTempPath(), "eri-render.html");
            File.WriteAllText(dump, html ?? string.Empty);
            Console.WriteLine("render.dump=" + dump);
        }
        catch (Exception ex)
        {
            Console.WriteLine("render.error=" + ex.GetType().Name + ": " + ex.Message);
            _exitCode = 2;
        }
        finally
        {
            Console.Out.Flush();
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown(_exitCode);
            }
        }
    }
}
