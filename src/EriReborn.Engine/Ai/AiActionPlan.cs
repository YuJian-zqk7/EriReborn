using System.Text.Json;

namespace EriReborn.Engine.Ai;

/// <summary>
/// The only things a model is allowed to propose.
///
/// <para>
/// A deliberately closed list. The model never produces a command, a path, a
/// registry key or a URL: it names an <b>intent</b> against something the catalog
/// already knows about, and the application decides what that means. Anything not
/// on this list cannot be expressed, so it cannot be executed by accident
/// (spec 57/59).
/// </para>
/// </summary>
public enum AiActionKind
{
    /// <summary>Install a catalog entry by id.</summary>
    Install,

    /// <summary>Record a detection hint (an ARP pattern) for a catalog entry.</summary>
    Hint,
}

public sealed record AiProposedAction(AiActionKind Kind, string Target, string? Value, string Reason);

/// <summary>A proposal that was refused before the user ever saw it.</summary>
public sealed record AiActionRejection(string Target, string Reason);

/// <summary>
/// What the model said, after it has been checked against reality.
///
/// <para>
/// Rejections are carried rather than dropped: a plan that quietly shrank between
/// the model and the screen is a plan the user is approving without knowing it.
/// </para>
/// </summary>
public sealed record AiActionPlan(
    string Summary,
    IReadOnlyList<AiProposedAction> Accepted,
    IReadOnlyList<AiActionRejection> Rejected)
{
    public bool HasActions => Accepted.Count > 0;
}

/// <summary>
/// Reads a proposed plan and refuses everything that is not on the list.
/// </summary>
public static class AiActionPlanner
{
    /// <summary>How many actions one plan may carry. A wall of changes is not a review.</summary>
    public const int MaxActions = 10;

    /// <summary>The contract the model is told to answer with.</summary>
    public const string ContractHint =
        "只输出 JSON：{\"summary\":\"理由\",\"actions\":[{\"kind\":\"install\"|\"hint\",\"target\":\"目录条目 id\","
        + "\"value\":\"仅 hint 需要\",\"reason\":\"理由\"}]}。"
        + "kind 只能是这两个之一；target 必须是清单里真实存在的 id。"
        + "不要输出命令、路径、注册表项或下载地址，也不要输出清单以外的条目。";

    /// <summary>
    /// Parses the model's answer.
    /// </summary>
    /// <param name="json">What the model returned.</param>
    /// <param name="knownIds">The ids the catalog actually has.</param>
    /// <param name="maxActions">Override for tests and for callers that want a smaller review.</param>
    public static AiActionPlan Parse(
        string? json,
        IReadOnlySet<string> knownIds,
        int maxActions = MaxActions)
    {
        ArgumentNullException.ThrowIfNull(knownIds);

        var accepted = new List<AiProposedAction>();
        var rejected = new List<AiActionRejection>();

        if (string.IsNullOrWhiteSpace(json))
        {
            return new AiActionPlan("（模型没有返回内容）", accepted, rejected);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(ExtractJsonObject(json));
        }
        catch (JsonException ex)
        {
            return new AiActionPlan(
                "（模型的回答不是可解析的 JSON）",
                accepted,
                new[] { new AiActionRejection("(整份回答)", ex.Message) });
        }

        // Declared outside the using block: the final return needs it too, and a
        // summary that only survives inside one branch is a summary nobody reads.
        var summary = string.Empty;

        using (document)
        {
            var root = document.RootElement;
            summary = root.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString() ?? string.Empty
                : string.Empty;

            if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            {
                return new AiActionPlan(summary, accepted, rejected);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var limit = Math.Max(0, maxActions);

            foreach (var entry in actions.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    rejected.Add(new AiActionRejection(entry.ToString(), "不是一个动作对象。"));
                    continue;
                }

                var kindText = Text(entry, "kind");
                var target = Text(entry, "target");
                var value = Text(entry, "value");
                var reason = Text(entry, "reason") ?? string.Empty;

                if (string.IsNullOrWhiteSpace(target))
                {
                    rejected.Add(new AiActionRejection("(缺少 target)", "动作没有指定目标。"));
                    continue;
                }

                if (!TryParseKind(kindText, out var kind))
                {
                    // Saying "unsupported" is the point: a model that invents a kind is
                    // testing whether anything off the list gets executed.
                    rejected.Add(new AiActionRejection(target, $"不支持的动作类型 '{kindText}'。"));
                    continue;
                }

                if (!knownIds.Contains(target))
                {
                    rejected.Add(new AiActionRejection(target, "清单里没有这个条目。"));
                    continue;
                }

                if (!seen.Add($"{kind}:{target}"))
                {
                    rejected.Add(new AiActionRejection(target, "同一个目标被重复提议。"));
                    continue;
                }

                if (kind == AiActionKind.Hint && string.IsNullOrWhiteSpace(value))
                {
                    rejected.Add(new AiActionRejection(target, "hint 没有给出匹配模式。"));
                    continue;
                }

                if (accepted.Count >= limit)
                {
                    rejected.Add(new AiActionRejection(target, $"一次最多 {limit} 项。"));
                    continue;
                }

                accepted.Add(new AiProposedAction(kind, target, value, reason));
            }
        }

        return new AiActionPlan(summary, accepted, rejected);
    }

    private static bool TryParseKind(string? text, out AiActionKind kind)
    {
        kind = AiActionKind.Install;

        if (string.Equals(text, "install", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(text, "hint", StringComparison.OrdinalIgnoreCase))
        {
            kind = AiActionKind.Hint;
            return true;
        }

        return false;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Models like to wrap JSON in prose or a code fence, and the prose often has braces of its own —
    /// <c>"plugins are files like { this }"</c> before the real object, or a trailing example after it.
    /// Taking the first <c>{</c> and the last <c>}</c> therefore failed on answers that were perfectly
    /// good JSON: the slice started in a sentence and ended in an example, and the parse error was
    /// reported as the model's fault.
    /// </summary>
    /// <remarks>
    /// A balanced object is read instead, so braces inside strings are text rather than structure, and
    /// when the answer contains more than one object the first one that actually parses is returned.
    /// Only when nothing parses does the caller see a parse error, and it is then about the model's own
    /// JSON rather than about the surrounding prose.
    /// </remarks>
    internal static string ExtractJsonObject(string text) => ExtractObject(text).Text;

    /// <summary>
    /// Reads the answer's JSON and says where the object came from, because "开头就是对象" and "从答案
    /// 中间捡到一个能解析的对象" are different answers that used to look identical here.
    /// </summary>
    /// <remarks>
    /// An answer cut off at the output limit never closes the object it opened with. Falling back to the
    /// first object that parses then hands the caller a fragment — a nested entry, which has the right
    /// shape and none of the content — and every check downstream reports something about the fragment
    /// ("草稿没有给出插件名") rather than about the answer having been cut in half. Naming the case is
    /// what lets the caller say "被截断了" instead.
    /// </remarks>
    internal static JsonObjectExtraction ExtractObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObjectExtraction(text, JsonObjectSource.None);
        }

        var first = text.IndexOf('{');
        if (first < 0)
        {
            return new JsonObjectExtraction(text, JsonObjectSource.None);
        }

        var outer = ReadBalancedObject(text, first);
        if (outer is null)
        {
            // The object the answer opened with never closed, so it was cut off and whatever is nested
            // inside it is a fragment. The whole text goes back: the parse error it produces is about the
            // model's JSON, which is the right thing to be told about.
            return new JsonObjectExtraction(text, JsonObjectSource.CutOff);
        }

        if (Parses(outer))
        {
            return new JsonObjectExtraction(outer, JsonObjectSource.Outer);
        }

        // A brace in the prose, or an example the model showed before the real answer. Keep looking
        // rather than giving up on the object that follows.
        for (var start = text.IndexOf('{', first + 1); start >= 0; start = text.IndexOf('{', start + 1))
        {
            if (ReadBalancedObject(text, start) is { } candidate && Parses(candidate))
            {
                return new JsonObjectExtraction(candidate, JsonObjectSource.Inner);
            }
        }

        // Nothing parsed: the first object is still the most useful thing to complain about.
        return new JsonObjectExtraction(outer, JsonObjectSource.Inner);
    }

    private static bool Parses(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads one whole JSON object starting at <paramref name="start"/>, or null when its braces never
    /// balance.
    /// </summary>
    /// <remarks>
    /// Braces inside a string are characters, not structure: a file or a resource whose name contains
    /// one would otherwise close the object early and truncate the JSON into something unparseable.
    /// </remarks>
    private static string? ReadBalancedObject(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return text[start..(i + 1)];
                    }

                    break;
            }
        }

        return null;
    }
}

/// <summary>Where the JSON an answer was read as actually came from.</summary>
internal enum JsonObjectSource
{
    /// <summary>There was no object to read: the answer was prose, empty, or an error.</summary>
    None,

    /// <summary>The answer opened an object and never closed it — it was cut off mid-draft.</summary>
    CutOff,

    /// <summary>The answer opened with an object, and that object parsed.</summary>
    Outer,

    /// <summary>An object parsed, but it was not the one the answer opened with.</summary>
    Inner,
}

/// <summary>
/// The object read out of an answer, and where it came from. The two travel together because an answer
/// cut off at the output limit still yields an object — a nested one — and a caller that cannot tell
/// that apart from a real answer reports the fragment's problems as if they were the answer's.
/// </summary>
internal readonly record struct JsonObjectExtraction(string Text, JsonObjectSource Source);
