using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace EriReborn.Mobile;

/// <summary>
/// Android entry activity. The composition root runs in
/// EriRebornAndroidApplication.OnCreate so the host is ready before the
/// Avalonia single view is created.
/// </summary>
[Activity(
    Name = "com.erireborn.app.MainActivity",
    Label = "EriReborn",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public class MainActivity : AvaloniaMainActivity<EriReborn.UI.Avalonia.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder);

    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The window only exists now, so this is the earliest moment the reserved
        // area can be asked about. Before it, the service reports zero — the honest
        // answer, not a guess pushed into the layout.
        if (EriReborn.UI.Avalonia.App.Host?.Platform.SafeArea is EriReborn.Platform.Android.AndroidSafeAreaService safeArea)
        {
            safeArea.Attach(this);
        }
    }

    /// <summary>
    /// Android's back gesture walks the page trail before it leaves the app.
    ///
    /// <para>
    /// Exiting straight from a page the user just opened reads as a crash, and on
    /// a platform with no window chrome the gesture is the only way back at all.
    /// Only when there is nowhere left to go does the platform's own behaviour run.
    /// </para>
    /// </summary>
    public override void OnBackPressed()
    {
        if (EriReborn.UI.Avalonia.App.Main is { } shell && shell.TryGoBack())
        {
            return;
        }

        base.OnBackPressed();
    }
}
