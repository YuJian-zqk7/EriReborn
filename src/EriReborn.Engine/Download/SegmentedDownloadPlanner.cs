namespace EriReborn.Engine.Download;

/// <summary>One byte range to fetch, as an HTTP Range would express it.</summary>
public sealed record ByteRange(long Start, long Length)
{
    public long EndInclusive => Start + Length - 1;

    /// <summary>Exclusive end, which is what file seeking wants.</summary>
    public long EndExclusive => Start + Length;

    public override string ToString() => $"{Start}-{EndInclusive}";
}

/// <summary>
/// When and how to split a download into parallel ranges.
///
/// <para>
/// Disabled by default: a second connection is not free, and for a small file it
/// is slower than one stream. Turning it on is a decision, not a default.
/// </para>
/// </summary>
public sealed record SegmentOptions(
    bool Enabled = false,
    long MinimumSize = 8L * 1024 * 1024,
    int MaxSegments = 4,
    long MinimumSegmentSize = 1L * 1024 * 1024);

/// <summary>
/// Splits a known-length download into non-overlapping, gapless ranges.
///
/// <para>
/// This is pure arithmetic on purpose. The failure mode of segmented downloading
/// is not a slow download, it is a file assembled from ranges that overlap or
/// leave a hole — and that produces a plausible-looking file with the wrong
/// bytes. Keeping the arithmetic testable is what makes that checkable.
/// </para>
/// </summary>
public static class SegmentedDownloadPlanner
{
    /// <summary>
    /// Ranges covering exactly [0, totalBytes). An <b>empty</b> result means the
    /// size is unknown, so the caller must not segment: it has no idea where the
    /// middle is.
    /// </summary>
    public static IReadOnlyList<ByteRange> Plan(long totalBytes, SegmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (totalBytes <= 0)
        {
            return Array.Empty<ByteRange>();
        }

        var whole = new[] { new ByteRange(0, totalBytes) };

        if (!options.Enabled || totalBytes < options.MinimumSize)
        {
            return whole;
        }

        // Never more pieces than the minimum segment size allows, and never more
        // than the caller asked for.
        var bySize = (int)Math.Min(int.MaxValue, totalBytes / Math.Max(1, options.MinimumSegmentSize));
        var count = Math.Clamp(bySize, 1, Math.Max(1, options.MaxSegments));

        if (count <= 1)
        {
            return whole;
        }

        // Distribute the remainder over the leading segments so the total is exact.
        var baseLength = totalBytes / count;
        var remainder = totalBytes % count;

        var ranges = new List<ByteRange>(count);
        var start = 0L;

        for (var index = 0; index < count; index++)
        {
            var length = baseLength + (index < remainder ? 1 : 0);
            ranges.Add(new ByteRange(start, length));
            start += length;
        }

        return ranges;
    }

    /// <summary>
    /// True only when the plan is a real partition of the whole: starts at zero,
    /// no gaps, no overlaps, ends exactly at the end. Checked rather than assumed,
    /// because every failure here yields a corrupt file rather than an error.
    /// </summary>
    public static bool CoversExactly(IReadOnlyList<ByteRange> ranges, long totalBytes)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        if (totalBytes <= 0)
        {
            return ranges.Count == 0;
        }

        var cursor = 0L;
        foreach (var range in ranges)
        {
            if (range.Start != cursor || range.Length <= 0)
            {
                return false;
            }

            cursor += range.Length;
        }

        return cursor == totalBytes;
    }
}
