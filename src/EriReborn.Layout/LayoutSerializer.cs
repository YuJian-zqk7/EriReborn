using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EriReborn.Layout;

/// <summary>
/// Saves, loads, imports and exports layout documents as JSON. This is the
/// interchange format the editor persists (spec 38/39).
/// </summary>
public static class LayoutSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Without this the default encoder escapes every non-ASCII character,
        // which would make a saved layout unreadable for CJK text.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(LayoutDocument document) => JsonSerializer.Serialize(document, Options);

    public static LayoutDocument Deserialize(string json)
        => JsonSerializer.Deserialize<LayoutDocument>(json, Options)
           ?? throw new InvalidDataException("布局文档为空或格式不正确。");

    public static void Save(LayoutDocument document, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Serialize(document));
    }

    public static LayoutDocument Load(string path) => Deserialize(File.ReadAllText(path));
}
