using System;
using Avalonia.Controls;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Where the shared UI asks for a real browser kernel.
///
/// <para>
/// The shared UI cannot reference WebView2 itself (its WPF dependency clashes with the net8.0 build
/// that also produces Android), so the Windows app installs a factory here at startup. With no
/// factory the caller falls back to the manual credential entry.
/// </para>
/// </summary>
public static class BrowserSlot
{
    /// <summary>Installed by the Windows app: builds a kernel-hosted control for a page.</summary>
    public static Func<Uri, Control?>? Factory { get; set; }

    /// <summary>
    /// Installed by the Windows app: builds the reader's local HTML surface. The returned
    /// control also implements <see cref="IReaderWebView"/> for pushing pages and receiving
    /// selection messages.
    /// </summary>
    public static Func<Control?>? ReaderFactory { get; set; }

    /// <summary>True when a kernel is available in this build/platform.</summary>
    public static bool IsAvailable => Factory is not null;

    /// <summary>
    /// Builds the kernel control for a page, or null when this build has none. The control is
    /// returned directly rather than wrapped: an extra ContentControl between the window and the
    /// native host only adds a way for the host to never be materialised.
    /// </summary>
    public static Control? Create(Uri page)
    {
        if (Factory is null)
        {
            BrowserLog.Write("factory: not installed in this build");
            return null;
        }

        try
        {
            var control = Factory(page);
            BrowserLog.Write("factory: produced " + (control?.GetType().Name ?? "null") + " for " + page);
            return control;
        }
        catch (Exception ex)
        {
            BrowserLog.Write("factory threw: " + ex);
            return null;
        }
    }

    /// <summary>
    /// Builds the reader's HTML surface (also an <see cref="IReaderWebView"/>), or null when
    /// this build has no browser kernel.
    /// </summary>
    public static Control? CreateReader()
    {
        if (ReaderFactory is null)
        {
            BrowserLog.Write("reader factory: not installed in this build");
            return null;
        }

        try
        {
            var control = ReaderFactory();
            BrowserLog.Write("reader factory: produced " + (control?.GetType().Name ?? "null"));
            return control;
        }
        catch (Exception ex)
        {
            BrowserLog.Write("reader factory threw: " + ex);
            return null;
        }
    }
}
