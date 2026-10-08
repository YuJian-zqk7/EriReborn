using Avalonia;
using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.Cloud;
using EriReborn.Desktop.Browser;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Core.Diagnostics;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;
using EriReborn.Platform.Windows;
using EriReborn.UI.Avalonia;
using ShellApp = EriReborn.UI.Avalonia.App;

namespace EriReborn.Desktop;

/// <summary>
/// Process entry point only (spec 66/67). It sets up logging, builds the
/// composition root, and hands control to the Avalonia shell. Any startup
/// exception is captured, logged and shown; it is never swallowed.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Detect();
        paths.EnsureDirectories();

        // Diagnostic: render one URL in the embedded browser and report what came back.
        if (args.Length >= 2 && args[0] == "--render")
        {
            return RenderProbe.Run(args[1]);
        }

        AppLog.MinimumLevel = LogLevel.Info;
        AppLog.AddSink(new FileLogSink(paths.LogFile));
        AppLog.AddSink(new ConsoleLogSink());

        var log = AppLog.For("Startup");
        log.Info("startup.begin", $"EriReborn starting. assets={paths.AssetsRoot}");

        // 蓝奏 is a JavaScript challenge and 迅雷 mints its captcha token inside the page, so the
        // embedded browser has to exist before the cloud providers are assembled.
        CloudBrowser.Install(new EmbeddedBrowserChannel());
        log.Info("startup.browser", "Embedded browser channel installed.");

        try
        {
            IPlatformService platform = WindowsPlatformService.Create(AppLog.For("Platform"));
            log.Info("startup.platform", $"Platform '{platform.PlatformId}' created.");

            var windowsPlatform = (WindowsPlatformService)platform;
            var host = AppHost.CreateAsync(
                    paths,
                    platform,
                    windowsPlatform.CloudRegistry,
                    log,
                    downloadEngines: windowsPlatform.Engines)
                .GetAwaiter()
                .GetResult();

            ShellApp.Host = host;
            ShellApp.Main = new MainViewModel(host, new NavigationService());
            log.Info("startup.composed", "Composition root completed; launching shell.");
        }
        catch (Exception ex)
        {
            var kind = StartupReport.Classify(ex);
            log.Fatal("startup.failed", $"Startup failed ({kind}).", ex);
            ShellApp.StartupFailure = StartupReport.Failure(kind, $"启动失败：{ex.Message}", ex, paths.LogFile);
        }

        try
        {
            // The Windows kernel is installed here: the shared UI only knows the abstraction.
            EriReborn.UI.Avalonia.Controls.BrowserSlot.Factory = page =>
                new EriReborn.Desktop.Browser.WebViewHost { Source = page };

            // 阅读器正文用独立的本地 HTML 内核表面。
            EriReborn.UI.Avalonia.Controls.BrowserSlot.ReaderFactory = () =>
                new EriReborn.Desktop.Browser.ReaderWebViewHost();

            // Two nets the shell's own try/catch cannot reach: an exception thrown on a thread the shell does
            // not own, and one nobody waited for. Without them the process can end with nothing in the log at
            // all — and an empty log is indistinguishable from a clean exit, which is exactly how one crash
            // report became unreadable (spec 58/69).
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                log.Fatal(
                    "appdomain.unhandled",
                    "An unhandled exception is ending the process.",
                    e.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (_, e) =>
                log.Fatal("task.unobserved", "A task failed and nobody observed it.", e.Exception);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            log.Info("startup.exit", "Application exited normally.");
            return ShellApp.StartupFailure is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Fatal("startup.shell", "The UI shell terminated unexpectedly.", ex);
            return 2;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ShellApp>()
            .UsePlatformDetect()
            .LogToTrace();
}
