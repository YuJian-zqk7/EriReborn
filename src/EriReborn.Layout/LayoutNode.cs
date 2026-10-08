using System.Text.Json.Serialization;

namespace EriReborn.Layout;

/// <summary>Element kinds a schema-driven layout may contain (spec 39).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutNodeType
{
    Page,
    Container,
    Stack,
    Grid,
    Text,
    Button,
    Image,
    List,
    Input,
    Progress,
    Toggle,
}

/// <summary>
/// One element of a schema-driven layout. Geometry is absolute within the
/// parent's coordinate space, which is what makes drag, move and resize
/// meaningful without editing XAML (spec 38/39).
/// </summary>
public sealed record LayoutNode
{
    public const double MinSize = 8;

    public required string Id { get; init; }

    public required LayoutNodeType Type { get; init; }

    public string? Text { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; } = 160;

    public double Height { get; init; } = 32;

    public bool Visible { get; init; } = true;

    public double? FontSize { get; init; }

    public string? Color { get; init; }

    /// <summary>Optional data binding name, resolved by the host page.</summary>
    public string? Binding { get; init; }

    /// <summary>Extra schema properties (column count for Grid, and so on).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; }
        = new Dictionary<string, string>();

    public IReadOnlyList<LayoutNode> Children { get; init; } = Array.Empty<LayoutNode>();

    public static LayoutNode CreateDefault(string id, LayoutNodeType type) => new()
    {
        Id = id,
        Type = type,
        Text = type switch
        {
            LayoutNodeType.Text => "文本",
            LayoutNodeType.Button => "按钮",
            LayoutNodeType.Input => string.Empty,
            LayoutNodeType.Toggle => "开关",
            _ => null,
        },
        Width = type switch
        {
            LayoutNodeType.Page => 900,
            LayoutNodeType.Grid => 420,
            LayoutNodeType.Stack => 260,
            LayoutNodeType.List => 360,
            LayoutNodeType.Progress => 240,
            LayoutNodeType.Image => 160,
            LayoutNodeType.Container => 280,
            _ => 160,
        },
        Height = type switch
        {
            LayoutNodeType.Page => 600,
            LayoutNodeType.Grid => 200,
            LayoutNodeType.Stack => 180,
            LayoutNodeType.List => 220,
            LayoutNodeType.Image => 120,
            _ => 32,
        },
    };
}

/// <summary>A complete page layout (spec 39).</summary>
public sealed record LayoutDocument
{
    public int SchemaVersion { get; init; } = 1;

    public required string Page { get; init; }

    public IReadOnlyList<LayoutNode> Nodes { get; init; } = Array.Empty<LayoutNode>();

    public static LayoutDocument Empty(string page = "Home") => new() { Page = page };
}
