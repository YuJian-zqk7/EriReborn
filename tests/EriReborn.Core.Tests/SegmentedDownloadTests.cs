using EriReborn.Engine.Download;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Segmented downloading fails quietly: ranges that overlap or leave a hole
/// produce a file that looks fine and is wrong. Every test here is about the
/// partition being exact (spec 24).
/// </summary>
public sealed class SegmentedDownloadTests
{
    private static readonly SegmentOptions On = new(
        Enabled: true,
        MinimumSize: 1000,
        MaxSegments: 4,
        MinimumSegmentSize: 100);

    [Fact]
    public void A_wide_range_of_sizes_is_partitioned_exactly()
    {
        // The whole point: whatever the size, the pieces must add up to it with no
        // gap and no overlap. Swept rather than sampled, because off-by-one errors
        // hide at specific remainders.
        for (var total = 1L; total <= 5000; total++)
        {
            var ranges = SegmentedDownloadPlanner.Plan(total, On);

            Assert.True(
                SegmentedDownloadPlanner.CoversExactly(ranges, total),
                $"总数 {total} 的分段不是精确划分：{string.Join(", ", ranges)}");
        }
    }

    [Fact]
    public void Awkward_large_sizes_are_partitioned_exactly()
    {
        foreach (var total in new long[]
                 {
                     8L * 1024 * 1024,
                     8L * 1024 * 1024 + 1,
                     10_000_000_007,
                     1L << 40,
                     (1L << 40) + 7,
                 })
        {
            var ranges = SegmentedDownloadPlanner.Plan(total, On);

            Assert.True(
                SegmentedDownloadPlanner.CoversExactly(ranges, total),
                $"总数 {total} 的分段不是精确划分：{string.Join(", ", ranges)}");
        }
    }

    [Fact]
    public void Ranges_never_overlap_and_never_leave_a_gap()
    {
        var ranges = SegmentedDownloadPlanner.Plan(1000, On);

        for (var index = 1; index < ranges.Count; index++)
        {
            Assert.Equal(ranges[index - 1].EndExclusive, ranges[index].Start);
        }

        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(1000, ranges[^1].EndExclusive);
    }

    [Fact]
    public void Segmentation_is_off_until_it_is_turned_on()
    {
        // A second connection is not free; enabling it is a decision.
        var ranges = SegmentedDownloadPlanner.Plan(100L * 1024 * 1024, new SegmentOptions());

        Assert.Single(ranges);
        Assert.Equal(new ByteRange(0, 100L * 1024 * 1024), ranges[0]);
    }

    [Fact]
    public void A_small_file_is_left_to_a_single_stream()
    {
        var options = new SegmentOptions(Enabled: true, MinimumSize: 1000, MaxSegments: 4, MinimumSegmentSize: 100);

        Assert.Single(SegmentedDownloadPlanner.Plan(999, options));
        Assert.Single(SegmentedDownloadPlanner.Plan(1000 - 1, options));
    }

    [Fact]
    public void An_unknown_size_is_not_segmented()
    {
        // With no total there is no middle to split at.
        Assert.Empty(SegmentedDownloadPlanner.Plan(0, On));
        Assert.Empty(SegmentedDownloadPlanner.Plan(-5, On));

        // An empty plan covers an empty file exactly — it just cannot cover a real one.
        Assert.True(SegmentedDownloadPlanner.CoversExactly(Array.Empty<ByteRange>(), 0));
        Assert.False(SegmentedDownloadPlanner.CoversExactly(Array.Empty<ByteRange>(), 10));
    }

    [Fact]
    public void The_number_of_pieces_respects_both_limits()
    {
        var options = new SegmentOptions(Enabled: true, MinimumSize: 1000, MaxSegments: 4, MinimumSegmentSize: 100);

        // Room for many pieces, but the caller allows four.
        Assert.Equal(4, SegmentedDownloadPlanner.Plan(100_000, options).Count);

        // The minimum segment size is the binding limit here: 1000 / 250 = 4.
        var tighter = options with { MinimumSegmentSize = 250 };
        Assert.Equal(4, SegmentedDownloadPlanner.Plan(1000, tighter).Count);

        // 1000 / 400 = 2 pieces, even though four would be allowed.
        var looser = options with { MinimumSegmentSize = 400 };
        Assert.Equal(2, SegmentedDownloadPlanner.Plan(1000, looser).Count);
    }

    [Fact]
    public void No_piece_is_smaller_than_the_minimum_segment_size()
    {
        var options = new SegmentOptions(Enabled: true, MinimumSize: 1000, MaxSegments: 8, MinimumSegmentSize: 300);

        foreach (var total in new long[] { 1000, 1200, 1500, 2400, 3000, 5000, 9999 })
        {
            var ranges = SegmentedDownloadPlanner.Plan(total, options);

            if (ranges.Count == 1)
            {
                continue;
            }

            foreach (var range in ranges)
            {
                Assert.True(range.Length >= 300, $"总数 {total} 出现了过短的段：{range}");
            }
        }
    }

    [Fact]
    public void A_zero_or_negative_piece_limit_does_not_produce_nothing()
    {
        // A misconfigured limit must not silently mean "download zero bytes".
        var options = new SegmentOptions(Enabled: true, MinimumSize: 1000, MaxSegments: 0, MinimumSegmentSize: 100);

        var ranges = SegmentedDownloadPlanner.Plan(10_000, options);

        Assert.Single(ranges);
        Assert.True(SegmentedDownloadPlanner.CoversExactly(ranges, 10_000));
    }

    [Fact]
    public void The_plan_is_deterministic()
    {
        var first = SegmentedDownloadPlanner.Plan(123_456, On);
        var second = SegmentedDownloadPlanner.Plan(123_456, On);

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_check_rejects_a_plan_with_a_hole_or_an_overlap()
    {
        // Proving the guard itself works: without this, a passing suite could mean
        // nothing.
        Assert.False(SegmentedDownloadPlanner.CoversExactly(new[] { new ByteRange(0, 5), new ByteRange(6, 5) }, 10));
        Assert.False(SegmentedDownloadPlanner.CoversExactly(new[] { new ByteRange(0, 5), new ByteRange(4, 6) }, 10));
        Assert.False(SegmentedDownloadPlanner.CoversExactly(new[] { new ByteRange(0, 5) }, 10));
        Assert.False(SegmentedDownloadPlanner.CoversExactly(new[] { new ByteRange(1, 9) }, 10));
        Assert.True(SegmentedDownloadPlanner.CoversExactly(new[] { new ByteRange(0, 5), new ByteRange(5, 5) }, 10));
    }

    [Fact]
    public void Desktop_segments_and_mobile_keeps_one_stream()
    {
        // The phone may be on metered data; a second connection is a cost the user
        // did not ask for.
        Assert.True(SegmentPolicy.ForPlatform("windows").Enabled);
        Assert.False(SegmentPolicy.ForPlatform("android").Enabled);
        Assert.False(SegmentPolicy.Mobile.Enabled);

        // An unknown platform is treated as a desktop rather than silently having
        // the feature switched off.
        Assert.True(SegmentPolicy.ForPlatform("linux").Enabled);
        Assert.True(SegmentPolicy.ForPlatform(null).Enabled);
    }

    [Fact]
    public void A_range_is_expressible_as_an_http_header()
    {
        var range = new ByteRange(100, 50);

        Assert.Equal(149, range.EndInclusive);
        Assert.Equal(150, range.EndExclusive);
        Assert.Equal("100-149", range.ToString());
    }
}
