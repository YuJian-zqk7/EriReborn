namespace EriReborn.Persona;

/// <summary>
/// What actually happened, before any voice is applied.
///
/// These are neutral facts. A persona decides how to say them, never what they
/// are (spec 103/174).
/// </summary>
public enum PersonaState
{
    Success,
    Info,
    Warning,
    Error,
    Progress,
    Waiting,
    Empty,

    /// <summary>Genuinely undetermined. Never to be spoken of as "missing".</summary>
    Unknown,

    /// <summary>Not possible here. Never to be spoken of as "missing".</summary>
    Unsupported,

    PermissionRequired,
    NetworkError,
    AuthenticationRequired,

    DownloadStarted,
    DownloadPaused,
    DownloadResumed,
    DownloadCompleted,
    DownloadFailed,

    InstallStarted,
    InstallCompleted,
    InstallFailed,

    DetectionStarted,
    DetectionCompleted,

    UpdateStarted,
    UpdateCompleted,
    UpdateFailed,
}

/// <summary>How a state should look on the character's face, when one is shown.</summary>
public enum PersonaEmotion
{
    Neutral,
    Happy,
    Proud,
    Worried,
    Annoyed,
    Sad,
    Sleepy,
}

/// <summary>
/// A fact and the wording around it, kept as two fields.
///
/// This is the whole point of separating them: a persona cannot replace the
/// technical text, because there is nowhere for it to go. Whatever the voice
/// says, <see cref="Technical"/> is exactly what the caller passed in.
/// </summary>
public sealed record PersonaMessage(
    PersonaState State,
    string Technical,
    string Voice,
    PersonaEmotion Emotion)
{
    /// <summary>The fact first, then the voice. Never the other way round.</summary>
    public string Display => string.IsNullOrWhiteSpace(Voice) ? Technical : $"{Technical}\n{Voice}";
}

/// <summary>The state vocabulary: keys, emotions, and the neutral wording used when a persona is silent.</summary>
public static class PersonaStates
{
    /// <summary>The pack key for a state, e.g. <c>install.failed</c>.</summary>
    public static string Key(PersonaState state) => state switch
    {
        PersonaState.Success => "state.success",
        PersonaState.Info => "state.info",
        PersonaState.Warning => "state.warning",
        PersonaState.Error => "state.error",
        PersonaState.Progress => "state.progress",
        PersonaState.Waiting => "state.waiting",
        PersonaState.Empty => "state.empty",
        PersonaState.Unknown => "state.unknown",
        PersonaState.Unsupported => "state.unsupported",
        PersonaState.PermissionRequired => "state.permission_required",
        PersonaState.NetworkError => "state.network_error",
        PersonaState.AuthenticationRequired => "state.authentication_required",
        PersonaState.DownloadStarted => "download.started",
        PersonaState.DownloadPaused => "download.paused",
        PersonaState.DownloadResumed => "download.resumed",
        PersonaState.DownloadCompleted => "download.completed",
        PersonaState.DownloadFailed => "download.failed",
        PersonaState.InstallStarted => "install.started",
        PersonaState.InstallCompleted => "install.completed",
        PersonaState.InstallFailed => "install.failed",
        PersonaState.DetectionStarted => "detection.started",
        PersonaState.DetectionCompleted => "detection.completed",
        PersonaState.UpdateStarted => "update.started",
        PersonaState.UpdateCompleted => "update.completed",
        PersonaState.UpdateFailed => "update.failed",
        _ => "state.info",
    };

    public static PersonaEmotion EmotionFor(PersonaState state) => state switch
    {
        PersonaState.Success or PersonaState.InstallCompleted or PersonaState.DownloadCompleted
            or PersonaState.DetectionCompleted or PersonaState.UpdateCompleted => PersonaEmotion.Proud,

        PersonaState.Info or PersonaState.Progress or PersonaState.DownloadStarted
            or PersonaState.DownloadResumed or PersonaState.InstallStarted or PersonaState.DetectionStarted
            or PersonaState.UpdateStarted => PersonaEmotion.Happy,

        PersonaState.Waiting or PersonaState.DownloadPaused => PersonaEmotion.Sleepy,

        PersonaState.PermissionRequired or PersonaState.AuthenticationRequired => PersonaEmotion.Worried,

        PersonaState.NetworkError or PersonaState.DownloadFailed or PersonaState.InstallFailed
            or PersonaState.UpdateFailed or PersonaState.Error => PersonaEmotion.Sad,

        PersonaState.Warning => PersonaEmotion.Annoyed,

        _ => PersonaEmotion.Neutral,
    };

    /// <summary>
    /// Used when a pack has no line for a state.
    ///
    /// It is deliberately flat rather than charming: a missing line must not read
    /// as the character having an opinion about something it was not told.
    /// </summary>
    public static string NeutralLine(PersonaState state) => state switch
    {
        PersonaState.Unknown => "目前无法判断。",
        PersonaState.Unsupported => "本平台不支持这一项。",
        _ => string.Empty,
    };

    public static IReadOnlyList<PersonaState> All { get; } = Enum.GetValues<PersonaState>();
}
