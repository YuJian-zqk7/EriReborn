namespace EriReborn.Platform.Abstractions;

/// <summary>
/// Implemented by detectors that keep a snapshot of slow platform data (for
/// example the installed-programs registry). The engine invalidates it after a
/// successful install or update so verification sees fresh state.
/// </summary>
public interface IDetectionCacheControl
{
    /// <summary>Drops any cached snapshot; the next detection re-reads the platform.</summary>
    void Invalidate();
}
