using Android.App;
using Android.Runtime;
using EriReborn.UI.Avalonia;

namespace EriReborn.Mobile;

/// <summary>
/// Builds the object graph before any activity exists, so the UI never has to
/// wait on services and startup failures can be shown instead of swallowed
/// (spec 66/67).
/// </summary>
[Application(Label = "EriReborn")]
public class EriRebornAndroidApplication : Application
{
    public EriRebornAndroidApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Bootstrap.Initialize();
    }
}
