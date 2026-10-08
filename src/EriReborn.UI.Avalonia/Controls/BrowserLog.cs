using System;
using System.IO;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Diagnostics for the embedded browser, written where the app can actually write.
///
/// <para>
/// The shell's own progress file lives under %AppData%\Roaming, which this machine denies; going
/// through the same denied folder is what made the first blank-page attempt undiagnosable. This
/// picks a writable target and falls back to the temp folder.
/// </para>
/// </summary>
public static class BrowserLog
{
    private static readonly object Gate = new();
    private static readonly string Target = Resolve();

    /// <summary>Where messages land (useful when reporting a failure).</summary>
    public static string Location => Target;

    private static string Resolve()
    {
        // App folder first: the app demonstrably can read and write its own install directory,
        // while this machine has denied per-user locations (the shell's Roaming progress file is
        // denied too).
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "EriReborn-webview", "browser.log"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EriReborn", "webview", "browser.log"),
            Path.Combine(Path.GetTempPath(), "EriReborn", "browser.log"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(candidate)!);
                File.AppendAllText(candidate, "--- session ---" + Environment.NewLine);
                return candidate;
            }
            catch
            {
                // Try the next one.
            }
        }

        return candidates[^1];
    }

    /// <summary>Appends one line. Never throws.</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Target, DateTimeOffset.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never take the UI down.
        }
    }
}
