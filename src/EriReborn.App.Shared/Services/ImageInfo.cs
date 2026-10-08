namespace EriReborn.App.Shared.Services;

/// <summary>
/// What a picture file is: its format and its pixel size.
///
/// <para>
/// Read from the file's own header rather than by decoding it. The editor only wants to say
/// "PNG，128 × 128" next to the element being edited, and decoding a 4K background to answer that
/// would cost far more than the answer is worth. The header of a PNG and of a JPEG both state the
/// size up front, and those are what a skin is made of.
/// </para>
/// </summary>
public static class ImageInfo
{
    /// <summary>A short description for the editor, e.g. "PNG · 128 × 128".</summary>
    public static string Describe(string? path)
    {
        var format = Format(path);
        var size = Size(path);

        return size is { } pixels
            ? $"{format} · {pixels.Width} × {pixels.Height}"
            : format;
    }

    /// <summary>The format, from the file name. Never null: an unknown one says so.</summary>
    public static string Format(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "未知";
        }

        var extension = Path.GetExtension(path).TrimStart('.');

        return extension.Length == 0
            ? "未知"
            : extension.ToUpperInvariant();
    }

    /// <summary>
    /// The pixel size, or null when the format does not state it in a header this can read. Null is
    /// a normal answer here — the editor shows the format and leaves the size out.
    /// </summary>
    public static (int Width, int Height)? Size(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[32];
            if (stream.Read(header) < 24)
            {
                return null;
            }

            // PNG: the IHDR chunk sits at a fixed offset, big-endian.
            if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            {
                return (ReadBigEndian(header[16..20]), ReadBigEndian(header[20..24]));
            }

            // GIF: little-endian, right after the signature.
            if (header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
            {
                return (header[6] | (header[7] << 8), header[8] | (header[9] << 8));
            }

            // BMP: little-endian, at offset 18.
            if (header[0] == 0x42 && header[1] == 0x4D)
            {
                return (ReadLittleEndian(header[18..22]), ReadLittleEndian(header[22..26]));
            }

            // JPEG: walk the segments to the frame header, which states the size.
            return stream.Length > 32 ? JpegSize(path) : null;
        }
        catch (Exception)
        {
            // An unreadable file simply has no size to report.
            return null;
        }
    }

    private static (int Width, int Height)? JpegSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            {
                return null;
            }

            while (true)
            {
                int marker;
                do
                {
                    marker = stream.ReadByte();
                    if (marker < 0)
                    {
                        return null;
                    }
                }
                while (marker != 0xFF);

                int kind;
                do
                {
                    kind = stream.ReadByte();
                }
                while (kind == 0xFF);

                if (kind < 0 || kind == 0xD8 || kind == 0x01 || (kind >= 0xD0 && kind <= 0xD7))
                {
                    continue;
                }

                var length = (stream.ReadByte() << 8) | stream.ReadByte();
                if (length < 2)
                {
                    return null;
                }

                // SOF0..SOF15, excluding the markers that are not frame headers.
                if (kind is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    stream.ReadByte();                                  // precision
                    var height = (stream.ReadByte() << 8) | stream.ReadByte();
                    var width = (stream.ReadByte() << 8) | stream.ReadByte();

                    return width > 0 && height > 0 ? (width, height) : null;
                }

                stream.Seek(length - 2, SeekOrigin.Current);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int ReadBigEndian(ReadOnlySpan<byte> bytes)
        => (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static int ReadLittleEndian(ReadOnlySpan<byte> bytes)
        => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
}
