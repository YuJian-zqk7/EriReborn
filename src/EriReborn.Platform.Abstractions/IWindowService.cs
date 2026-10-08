namespace EriReborn.Platform.Abstractions;

/// <summary>
/// Implemented by whatever actually owns the native window (the Avalonia
/// desktop host). Kept separate so the platform service never references a
/// UI framework (spec 5).
/// </summary>
public interface IWindowShellHost
{
    void Minimize();

    void ToggleMaximize();

    void Restore();

    void Close();

    void SetTitle(string title);

    void BeginDragMove();
}

/// <summary>
/// Window shell operations (spec 53). On a platform with no window shell the
/// operations report failure instead of silently pretending to work.
/// </summary>
public interface IWindowService
{
    bool IsSupported { get; }

    bool CanResize { get; }

    bool IsAttached { get; }

    void Attach(IWindowShellHost host);

    void Minimize();

    void ToggleMaximize();

    void Restore();

    void Close();

    void SetTitle(string title);

    void BeginDragMove();
}
