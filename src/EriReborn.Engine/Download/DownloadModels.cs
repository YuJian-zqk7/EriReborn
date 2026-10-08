using EriReborn.Core.Domain;

namespace EriReborn.Engine.Download;

public sealed record DownloadRequest
{
    public required string Url { get; init; }

    public required string DestinationPath { get; init; }

    /// <summary>Expected SHA-256 of the final, complete file (spec 24).</summary>
    public string? ExpectedSha256 { get; init; }

    public long? ExpectedSize { get; init; }

    public string? SoftwareId { get; init; }

    public string? Version { get; init; }

    public string? Architecture { get; init; }

    public string? ProviderId { get; init; }

    public string? FileName { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}

public enum DownloadState
{
    Success,
    ServedFromCache,

    /// <summary>The engine could not run at all, e.g. an optional tool is absent.</summary>
    EngineUnavailable,
    HttpError,
    RangeRestartRequired,
    HashMismatch,
    SizeMismatch,
    NetworkError,
    Cancelled,
}

public sealed record DownloadResult(
    DownloadState State,
    string Path,
    long BytesWritten,
    string? Sha256 = null,
    int? HttpStatusCode = null,
    bool Resumed = false,
    string? Message = null)
{
    public bool IsSuccess => State is DownloadState.Success or DownloadState.ServedFromCache;
}

/// <summary>Why a range request could not be honoured (spec 24).</summary>
public enum RangeDecision
{
    NoResumePossible,
    ResumeAccepted,
    RestartRequired,
}
