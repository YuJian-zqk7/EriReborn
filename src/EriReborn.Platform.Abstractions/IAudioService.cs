namespace EriReborn.Platform.Abstractions;

/// <summary>
/// Plays short sound effects. Implementations must be safe to call from any
/// thread and must not throw when the platform cannot play audio.
/// </summary>
public interface IAudioService
{
    /// <summary>Plays an audio file by absolute path. Silently no-ops on failure.</summary>
    void PlaySound(string filePath);
}
