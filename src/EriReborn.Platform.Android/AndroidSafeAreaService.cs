using Android.App;
using Android.Views;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

/// <summary>
/// Reports the space Android reserves for the status bar, the gesture bar and any
/// display cutout.
///
/// <para>
/// The service is created before there is an activity to ask, so it starts at
/// "nothing reserved" and is attached once the window exists. Before that moment
/// the answer is genuinely unknown, and saying otherwise would push a guess into
/// the layout.
/// </para>
///
/// <para>
/// <b>Not verified on a device.</b> The pixel-to-unit conversion and the
/// sanitising are tested; the values a real phone reports are not, because there
/// is no device here. Below API 30 the platform's own answer is not read at all,
/// and that is reported as zero rather than approximated.
/// </para>
/// </summary>
public sealed class AndroidSafeAreaService(IAppLogger log) : ISafeAreaService
{
    private SafeAreaInsets _current = SafeAreaInsets.None;
    private bool _warnedAboutApi;

    public SafeAreaInsets Current => _current;

    public event EventHandler<SafeAreaInsets>? Changed;

    /// <summary>Attaches to a live window and reports every change from then on.</summary>
    public void Attach(Activity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var decor = activity.Window?.DecorView;
        if (decor is null)
        {
            log.Warn("safearea.attach", "窗口还没有装饰视图，安全区暂时按 0 处理。");
            return;
        }

        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            // Reading the older API means using a deprecated member whose values are
            // not the same shape. Reporting zero is honest; guessing is not.
            if (!_warnedAboutApi)
            {
                _warnedAboutApi = true;
                log.Warn("safearea.api", "系统版本低于 API 30，本版本不读取系统保留区域（按 0 处理）。");
            }

            return;
        }

        var density = activity.Resources?.DisplayMetrics?.Density ?? 1;
        decor.SetOnApplyWindowInsetsListener(new InsetsListener(this, density));
        decor.RequestApplyInsets();
    }

    private void Update(SafeAreaInsets insets)
    {
        if (insets == _current)
        {
            return;
        }

        _current = insets;
        Changed?.Invoke(this, insets);
    }

    private sealed class InsetsListener(AndroidSafeAreaService owner, double density)
        : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            // The guard belongs here, not only at the call site: this callback runs
            // later, on a thread of the platform's choosing, and the analyser cannot
            // carry the earlier check across that boundary.
            if (!OperatingSystem.IsAndroidVersionAtLeast(30) || insets is null)
            {
                return insets!;
            }

            var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
            if (bars is not null)
            {
                owner.Update(SafeAreaInsets.FromPixels(bars.Top, bars.Right, bars.Bottom, bars.Left, density));
            }

            // Returning the insets unchanged keeps the system's own spacing working.
            return insets;
        }
    }
}
