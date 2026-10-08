namespace EriReborn.Core.Diagnostics;

/// <summary>Which subsystem failed during startup (spec 66).</summary>
public enum StartupFailureKind
{
    None,
    Resource,
    Dependency,
    Platform,
    Configuration,
    Unknown,
}

/// <summary>
/// Never let startup exceptions vanish into a blank window (spec 66).
/// The report is shown to the user and written to the log.
/// </summary>
public sealed record StartupReport(
    bool Succeeded,
    StartupFailureKind Kind,
    string Summary,
    string? Detail = null,
    string? StackTrace = null,
    string? LogPath = null)
{
    public static StartupReport Ok(string logPath) =>
        new(true, StartupFailureKind.None, "Startup completed.", LogPath: logPath);

    public static StartupReport Failure(StartupFailureKind kind, string summary, Exception? ex = null, string? logPath = null) =>
        new(false, kind, summary, ex?.Message, ex?.StackTrace, logPath);

    /// <summary>Classifies an exception so the user sees the right category.</summary>
    public static StartupFailureKind Classify(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException or FileLoadException => StartupFailureKind.Resource,
        TypeInitializationException or MissingMethodException or BadImageFormatException => StartupFailureKind.Dependency,
        PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException => StartupFailureKind.Platform,
        System.Text.Json.JsonException or FormatException or InvalidDataException => StartupFailureKind.Configuration,
        _ => StartupFailureKind.Unknown,
    };
}
