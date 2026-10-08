using System.Text.Json;

namespace EriReborn.Asset;

public sealed record AssetCanvas(int Width, int Height);

/// <summary>A rectangle inside a sheet, in source pixels.</summary>
public sealed record AssetRect(int X, int Y, int Width, int Height);

/// <summary>One sliced piece of a sheet (spec 50).</summary>
public sealed record AssetElement(string File, AssetRect? Rect, int OpaquePixels);

/// <summary>Animation metadata for a sheet that is a sprite strip (spec 51).</summary>
public sealed record AssetSprite(int FrameWidth, int FrameHeight, int FrameCount, int Fps, bool Loop);

/// <summary>
/// One registered asset sheet. Pages never hard-code file paths (spec 48);
/// they resolve an id through <see cref="AssetManager"/>.
/// </summary>
public sealed record AssetSheet
{
    public required string Id { get; init; }

    public string Skin { get; init; } = "brand";

    public string Platform { get; init; } = "all";

    public string Type { get; init; } = "control";

    public required string File { get; init; }

    public AssetCanvas? Canvas { get; init; }

    public bool Transparent { get; init; }

    /// <summary>Directory holding this sheet's sliced pieces, relative to the asset root.</summary>
    public string? SlicedDirectory { get; init; }

    public IReadOnlyList<AssetElement> Elements { get; init; } = Array.Empty<AssetElement>();

    /// <summary>Present when the sheet is an animation strip.</summary>
    public AssetSprite? Sprite { get; init; }

    /// <summary>
    /// Present when this sheet is meant to be drawn as a nine-slice frame rather
    /// than used as a picture. Without it a sheet is just an image, and guessing
    /// insets from the pixels would turn any sprite into a stretched border.
    /// </summary>
    public SliceInsets? Slice { get; init; }

    public bool IsNineSlice => Slice is not null;

    public bool IsAnimated => Sprite is { FrameCount: > 1 };

    /// <summary>Absolute path, filled in by the manager once the root is known.</summary>
    public string? ResolvedPath { get; init; }
}

public static class AssetManifestParser
{
    public static IReadOnlyList<AssetSheet> Parse(string json, string rootDirectory)
    {
        using var document = JsonDocument.Parse(json);
        var sheets = new List<AssetSheet>();

        // A document whose root is not an object cannot have a "sheets" property, and
        // asking anyway throws rather than answering. "[]", ""x"" and "42" are all
        // valid JSON, and none of them is a manifest with sheets in it.
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return sheets;
        }

        if (!document.RootElement.TryGetProperty("sheets", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return sheets;
        }

        foreach (var entry in array.EnumerateArray())
        {
            var file = Str(entry, "file");
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            SliceInsets? slice = null;
            if (entry.TryGetProperty("slice", out var sliceElement) && sliceElement.ValueKind == JsonValueKind.Object)
            {
                slice = new SliceInsets(
                    Int(sliceElement, "left") ?? 0,
                    Int(sliceElement, "top") ?? 0,
                    Int(sliceElement, "right") ?? 0,
                    Int(sliceElement, "bottom") ?? 0);
            }

            AssetCanvas? canvas = null;
            if (entry.TryGetProperty("canvas", out var canvasElement)
                && canvasElement.TryGetProperty("width", out var widthElement)
                && canvasElement.TryGetProperty("height", out var heightElement)
                && widthElement.TryGetInt32(out var width)
                && heightElement.TryGetInt32(out var height))
            {
                canvas = new AssetCanvas(width, height);
            }

            sheets.Add(new AssetSheet
            {
                Id = Str(entry, "id") ?? Path.GetFileNameWithoutExtension(file),
                Skin = Str(entry, "skin") ?? "brand",
                Platform = Str(entry, "platform") ?? "all",
                Type = Str(entry, "type") ?? "control",
                File = file,
                Canvas = canvas,
                Slice = slice,
                Transparent = entry.TryGetProperty("transparent", out var t) && t.ValueKind == JsonValueKind.True,

                // The manifest writes "slicedDir"; "sliced" is tolerated for older files.
                SlicedDirectory = Str(entry, "slicedDir") ?? Str(entry, "sliced"),
                Elements = ReadElements(entry, "elements"),
                Sprite = ReadSprite(entry, "sprite"),
                ResolvedPath = Path.GetFullPath(Path.Combine(rootDirectory, file.Replace('/', Path.DirectorySeparatorChar))),
            });
        }

        return sheets;
    }

    /// <summary>
    /// Elements are objects ({ file, rect, opaquePx }), not bare strings. Reading
    /// them as strings silently produced an empty list before.
    /// </summary>
    private static IReadOnlyList<AssetElement> ReadElements(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AssetElement>();
        }

        var elements = new List<AssetElement>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                // Tolerate a plain string list as well.
                if (item.GetString() is { Length: > 0 } simple)
                {
                    elements.Add(new AssetElement(simple, null, 0));
                }

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var file = Str(item, "file");
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            // "rect": null is legal JSON and was written by the library importer for
            // frames that carry no rectangle. Reading x off a null element throws,
            // and that throw used to take the whole manifest down with it — every
            // asset in the file vanished because one entry said null.
            AssetRect? rect = null;
            if (item.TryGetProperty("rect", out var rectElement)
                && rectElement.ValueKind == JsonValueKind.Object
                && rectElement.TryGetProperty("x", out var x)
                && rectElement.TryGetProperty("y", out var y)
                && rectElement.TryGetProperty("w", out var w)
                && rectElement.TryGetProperty("h", out var h)
                && x.TryGetInt32(out var rx)
                && y.TryGetInt32(out var ry)
                && w.TryGetInt32(out var rw)
                && h.TryGetInt32(out var rh))
            {
                rect = new AssetRect(rx, ry, rw, rh);
            }

            var opaque = item.TryGetProperty("opaquePx", out var opaqueElement) && opaqueElement.TryGetInt32(out var parsed)
                ? parsed
                : 0;

            elements.Add(new AssetElement(file, rect, opaque));
        }

        return elements;
    }

    private static AssetSprite? ReadSprite(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var sprite) || sprite.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!sprite.TryGetProperty("frameWidth", out var fw) || !fw.TryGetInt32(out var frameWidth)
            || !sprite.TryGetProperty("frameHeight", out var fh) || !fh.TryGetInt32(out var frameHeight)
            || !sprite.TryGetProperty("frameCount", out var fc) || !fc.TryGetInt32(out var frameCount)
            || frameWidth <= 0 || frameHeight <= 0 || frameCount <= 0)
        {
            return null;
        }

        var fps = sprite.TryGetProperty("fps", out var f) && f.TryGetInt32(out var parsedFps) && parsedFps > 0
            ? parsedFps
            : 8;

        var loop = !sprite.TryGetProperty("loop", out var l) || l.ValueKind != JsonValueKind.False;

        return new AssetSprite(frameWidth, frameHeight, frameCount, fps, loop);
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
