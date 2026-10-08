using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Core.Diagnostics;
using EriReborn.Core.Logging;
using EriReborn.Platform.Android;
using EriReborn.UI.Avalonia;
using ShellApp = EriReborn.UI.Avalonia.App;

namespace EriReborn.Mobile;

/// <summary>
/// Android composition root (spec 67). Runs on the Android main thread before
/// the first activity, which is why it blocks deliberately.
/// </summary>
internal static class Bootstrap
{
    public static void Initialize()
    {
        var logPath = Path.Combine(
            global::Android.App.Application.Context.FilesDir!.AbsolutePath,
            "logs",
            "erireborn.log");

        AppLog.MinimumLevel = LogLevel.Info;
        AppLog.AddSink(new FileLogSink(logPath));

        var log = AppLog.For("Startup");
        log.Info("startup.begin", "EriReborn Android starting.");

        try
        {
            var platform = AndroidPlatformService.Create(AppLog.For("Platform"));

            // Assets live inside the APK; extract them once so the rest of the
            // application can use ordinary paths (spec 17/18).
            var assetsRoot = platform.AssetProvisioner
                .EnsureAssetsAsync()
                .GetAwaiter()
                .GetResult();

            var context = global::Android.App.Application.Context;
            var paths = AppPaths.Detect(
                assetsOverride: assetsRoot,
                userDataOverride: Path.Combine(context.FilesDir!.AbsolutePath, "userdata"));

            var host = AppHost.CreateAsync(paths, platform, platform.CloudRegistry, log)
                .GetAwaiter()
                .GetResult();

            ShellApp.Host = host;
            ShellApp.Main = new MainViewModel(host, new NavigationService());
            log.Info("startup.composed", "Android composition root completed.");
        }
        catch (Exception ex)
        {
            var kind = StartupReport.Classify(ex);
            log.Fatal("startup.failed", $"Android startup failed ({kind}).", ex);
            ShellApp.StartupFailure = StartupReport.Failure(kind, $"启动失败：{ex.Message}", ex, logPath);
        }
    }
}
