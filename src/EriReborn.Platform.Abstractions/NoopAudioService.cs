namespace EriReborn.Platform.Abstractions;

/// <summary>
/// No-op audio for platforms that do not implement playback yet (Android) or
/// for tests. It never throws and does nothing, so callers can stay uniform.
/// </summary>
public sealed class NoopAudioService : IAudioService
{
    public void PlaySound(string filePath) { }
}
