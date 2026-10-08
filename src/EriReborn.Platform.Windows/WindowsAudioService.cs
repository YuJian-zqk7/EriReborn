using System.Runtime.InteropServices;
using EriReborn.Core.Logging;
using EriReborn.Platform.Abstractions;

namespace EriReborn.Platform.Windows;

/// <summary>
/// Plays sound effects through the Windows MCI interface, which handles MP3
/// without a native dependency. A failed call is logged and swallowed because
/// a missing sound must never break the job that triggered it.
/// </summary>
public sealed class WindowsAudioService : IAudioService
{
    private readonly IAppLogger _log;

    public WindowsAudioService(IAppLogger log)
    {
        _log = log;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, System.Text.StringBuilder? buffer, int bufferSize, IntPtr hwndCallback);

    public void PlaySound(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            var alias = "erireborn_sfx_" + Guid.NewGuid().ToString("N");
            var open = $"open \"{filePath}\" type mpegvideo alias {alias}";
            var play = $"play {alias} from 0";

            if (mciSendString(open, null, 0, IntPtr.Zero) != 0)
            {
                return;
            }

            mciSendString(play, null, 0, IntPtr.Zero);

            // Close the device once playback finishes so the file is not locked.
            // We do not wait here; the OS closes it asynchronously when the clip ends.
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000).ConfigureAwait(false);
                mciSendString($"close {alias}", null, 0, IntPtr.Zero);
            });
        }
        catch (Exception ex)
        {
            _log.Debug("audio", $"play sound failed: {ex.Message}");
        }
    }
}
