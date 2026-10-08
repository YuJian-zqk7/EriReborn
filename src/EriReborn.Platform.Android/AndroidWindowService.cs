using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Android;

/// <summary>
/// Android has no movable/resizable window shell; the platform reports that
/// honestly instead of pretending the desktop chrome exists (spec 53).
/// </summary>
public sealed class AndroidWindowService : IWindowService
{
    public bool IsSupported => false;

    public bool CanResize => false;

    public bool IsAttached => false;

    public void Attach(IWindowShellHost host)
    {
    }

    public void Minimize()
    {
    }

    public void ToggleMaximize()
    {
    }

    public void Restore()
    {
    }

    public void Close()
    {
    }

    public void SetTitle(string title)
    {
    }

    public void BeginDragMove()
    {
    }
}
