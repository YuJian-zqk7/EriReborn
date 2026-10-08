namespace EriReborn.Cloud;

/// <summary>Authentication state reported by a provider, based on real inputs only (spec 31).</summary>
public enum CloudAuthState
{
    /// <summary>The platform needs no credential for the requested operation.</summary>
    NotRequired,
    Authenticated,
    AuthRequired,
    Expired,
    Unsupported,
}

/// <summary>Provider-neutral error classification (spec 28).</summary>
public enum CloudErrorKind
{
    None,
    AuthRequired,
    RateLimited,
    Forbidden,
    NotFound,
    Unsupported,
    ParseFailure,
    Network,
    ProviderError,
}

/// <summary>
/// A provider-agnostic credential bundle. Values come from
/// <see cref="EriReborn.Platform.Abstractions.ICredentialStore"/>, never from
/// a plain-text config file (spec 31/59).
/// </summary>
public sealed record CloudCredential(
    string ProviderId,
    string? Token = null,
    string? Cookie = null,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAt = null)
{
    public bool HasAnySecret => !string.IsNullOrWhiteSpace(Token)
        || !string.IsNullOrWhiteSpace(Cookie)
        || !string.IsNullOrWhiteSpace(RefreshToken);
}

/// <summary>One entry inside a cloud folder.</summary>
public sealed record CloudFile(
    string Id,
    string Name,
    bool IsFolder,
    long? SizeBytes = null,
    string? ParentPath = null,
    DateTimeOffset? ModifiedAt = null,
    string? DirectDownloadUrl = null,
    string? Sha256 = null,
    // 123 云盘 download_info API 的 body 需要 Etag + S3KeyFlag + Size，三者都从 list 响应里
    // 解析后挂到 CloudFile 上，让 ResolveShareItemAsync（按 ID 取直链）能直接用——
    // 走 by-parent 快捷路径时不再经过根目录的 ResolveAsync，无法在调用现场补这些字段。
    string? Etag = null,
    string? S3KeyFlag = null);

public sealed record CloudListResult(
    bool Success,
    IReadOnlyList<CloudFile> Files,
    CloudErrorKind Error = CloudErrorKind.None,
    string? Message = null)
{
    public static CloudListResult Ok(IReadOnlyList<CloudFile> files) => new(true, files);

    public static CloudListResult Fail(CloudErrorKind error, string message) =>
        new(false, Array.Empty<CloudFile>(), error, message);
}

/// <summary>A resolved, ready-to-download file handle.</summary>
public sealed record CloudDownloadHandle(
    CloudFile File,
    string DownloadUrl,
    IReadOnlyDictionary<string, string>? Headers = null,
    bool IsHtmlParsed = false,
    string? Note = null);

public sealed record CloudResolveResult(
    bool Success,
    CloudDownloadHandle? Handle,
    CloudErrorKind Error = CloudErrorKind.None,
    string? Message = null)
{
    public static CloudResolveResult Ok(CloudDownloadHandle handle) => new(true, handle);

    public static CloudResolveResult Fail(CloudErrorKind error, string message) => new(false, null, error, message);
}
