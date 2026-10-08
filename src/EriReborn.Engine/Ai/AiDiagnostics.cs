using System.Net;

namespace EriReborn.Engine.Ai;

/// <summary>
/// The words shared by every provider when a call does not work out.
///
/// <para>
/// Kept in one place because two providers that describe the same failure differently is how a user ends
/// up unable to tell "wrong key" from "wrong address" — and because the difference between an
/// unimplemented provider and a broken one has to be visible (spec 58/69).
/// </para>
/// </summary>
internal static class AiDiagnostics
{
    /// <summary>Why a model listing failed, in terms the user can act on.</summary>
    internal static string ProbeReason(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "API Key 无效或未提供",
        HttpStatusCode.Forbidden => "API Key 无权访问该服务",
        HttpStatusCode.TooManyRequests => "请求过于频繁，已被限流",
        HttpStatusCode.NotFound => "该地址没有 /models 接口，请确认 Base URL",
        _ => "服务返回错误",
    };

    /// <summary>Why a completion failed, in terms the user can act on.</summary>
    internal static string CompletionReason(HttpStatusCode status, string endpoint) => status switch
    {
        HttpStatusCode.Unauthorized => "API Key 无效或未提供",
        HttpStatusCode.Forbidden => "API Key 无权访问该模型",
        HttpStatusCode.TooManyRequests => "请求过于频繁，已被限流",
        HttpStatusCode.NotFound => $"该地址没有 {endpoint} 接口，请确认 Base URL",
        _ => "服务返回错误",
    };

    /// <summary>An error body flattened to one line, short enough to read in a status bar.</summary>
    internal static string OneLine(string body, int max)
    {
        var trimmed = body.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return trimmed.Length == 0 ? string.Empty : trimmed[..Math.Min(max, trimmed.Length)];
    }

    /// <summary>An error body truncated with an ellipsis, for a panel with room to explain.</summary>
    internal static string Trailing(string body, int max)
    {
        var trimmed = body.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }
}
