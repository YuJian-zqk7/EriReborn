using System.Text.Json;

namespace EriReborn.Cloud;

/// <summary>
/// Reads the shape a cloud API actually sent, instead of the shape it was expected to send.
///
/// <para>
/// Every provider here answers with an envelope — <c>{"code":…,"data":…}</c> — and <c>data</c> is
/// <em>not</em> always an object: an unauthenticated or failed call answers with <c>null</c>, or with the
/// error in a different member entirely. Calling <c>TryGetProperty</c> on such an element throws
/// <see cref="InvalidOperationException"/>, and because a provider runs inside a page command, that
/// exception left the dispatcher and took the whole application down: signing in to a drive and being
/// refused was enough to close EriReborn. These helpers answer "no" instead of throwing, so a refused
/// call becomes a message about being refused.
/// </para>
/// </summary>
public static class CloudJson
{
    /// <summary>The member, when it is present and really an object.</summary>
    public static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;

        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var found)
            && found.ValueKind == JsonValueKind.Object
            && Assign(found, out value);
    }

    /// <summary>The member, when it is present and really an array.</summary>
    public static bool TryArray(JsonElement parent, string name, out JsonElement value)
    {
        value = default;

        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var found)
            && found.ValueKind == JsonValueKind.Array
            && Assign(found, out value);
    }

    /// <summary>The member, when it is present and really a string.</summary>
    public static bool TryString(JsonElement parent, string name, out string? value)
    {
        value = null;

        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var found)
            || found.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = found.GetString();
        return true;
    }

    /// <summary>The member as an integer, when it is present and really a number.</summary>
    public static bool TryInt(JsonElement parent, string name, out int value)
    {
        value = 0;

        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var found)
            && found.ValueKind == JsonValueKind.Number
            && found.TryGetInt32(out value);
    }

    /// <summary>
    /// The member as a 64-bit integer, when it is present and really a number. File and folder ids are
    /// numbers here, and a caller that reads them through this cannot be undone by one arriving as a string.
    /// </summary>
    public static long? TryInt64(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object
           && parent.TryGetProperty(name, out var found)
           && found.ValueKind == JsonValueKind.Number
           && found.TryGetInt64(out var value)
            ? value
            : null;

    /// <summary>
    /// The response code an envelope carries, or null when it carries none. Null is not success and not
    /// failure: a reply without a code is one whose error has to be judged some other way.
    /// </summary>
    public static int? Code(JsonElement root) => TryInt(root, "code", out var code) ? code : null;

    /// <summary>The message an envelope carries, when it carries one.</summary>
    public static string? Message(JsonElement root)
        => TryString(root, "message", out var message)
            ? message
            : TryString(root, "msg", out var shortMessage) ? shortMessage : null;

    private static bool Assign(JsonElement found, out JsonElement value)
    {
        value = found;
        return true;
    }
}
