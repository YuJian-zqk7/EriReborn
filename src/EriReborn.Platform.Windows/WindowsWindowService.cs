using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Windows;

/// <summary>
/// Windows window shell (spec 53). Before the host attaches a real window
/// every operation is refused and logged; it never reports a fake success.
/// </summary>
public sealed class WindowsWindowService(IAppLogger log) : IWindowService
{
    private readonly IAppLogger _log = log;
    private IWindowShellHost? _host;

    public bool IsSupported => true;

    public bool CanResize => true;

    public bool IsAttached => _host is not null;

    public void Attach(IWindowShellHost host)
    {
        _host = host;
        _log.Info("window.attach", "Window shell host attached.");
    }

    public void Minimize() => Invoke(nameof(Minimize), h => h.Minimize());

    public void ToggleMaximize() => Invoke(nameof(ToggleMaximize), h => h.ToggleMaximize());

    public void Restore() => Invoke(nameof(Restore), h => h.Restore());

    public void Close() => Invoke(nameof(Close), h => h.Close());

    public void SetTitle(string title) => Invoke(nameof(SetTitle), h => h.SetTitle(title));

    public void BeginDragMove() => Invoke(nameof(BeginDragMove), h => h.BeginDragMove());

    private void Invoke(string operation, Action<IWindowShellHost> action)
    {
        var host = _host;
        if (host is null)
        {
            _log.Warn("window.no_host", $"'{operation}' ignored: no window shell host is attached yet.");
            return;
        }

        try
        {
            action(host);
        }
        catch (Exception ex)
        {
            _log.Error("window.op", $"Window operation '{operation}' failed.", ex);
        }
    }
}
