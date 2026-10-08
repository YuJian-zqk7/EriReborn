using EriReborn.Asset;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Nine-slice pieces must tile their span exactly. An overlap or a one-pixel gap
/// produces a frame that looks almost right, which is the kind of defect nobody
/// reports and everybody notices (spec 107/113).
/// </summary>
public sealed class NineSliceTests
{
    /// <summary>Asserts the nine pieces cover a WxH area with no gap and no overlap.</summary>
    private static void AssertTiles(IReadOnlyList<PixelRect> pieces, int width, int height, string what)
    {
        // The whole grid goes into every message: a tiling failure is impossible to
        // reason about from one coordinate.
        var detail = what + " " + width + "x" + height + " [" +
            string.Join(" ", pieces.Select((piece, index) => NineSlice.Names[index] + "=" + piece)) + "]";

        Assert.Equal(NineSlice.PieceCount, pieces.Count);

        foreach (var piece in pieces)
        {
            Assert.True(piece.Width >= 0 && piece.Height >= 0, $"出现了负尺寸。{detail}");
            Assert.True(piece.X >= 0 && piece.Y >= 0, $"越界。{detail}");
            Assert.True(piece.Right <= width, $"横向越界。{detail}");
            Assert.True(piece.Bottom <= height, $"纵向越界。{detail}");
        }

        // Column boundaries come from the top row; every row must agree with them.
        var columns = pieces.Take(3).ToList();
        var cursor = 0;
        foreach (var column in columns)
        {
            Assert.True(column.X == cursor, $"列的起点有缝或重叠，期望 {cursor}，实际 {column.X}。{detail}");
            cursor = column.Right;
        }

        Assert.Equal(width, cursor);

        var rows = new[] { pieces[0], pieces[3], pieces[6] };
        cursor = 0;
        foreach (var row in rows)
        {
            Assert.True(row.Y == cursor, $"行的起点有缝或重叠，期望 {cursor}，实际 {row.Y}。{detail}");
            cursor = row.Bottom;
        }

        Assert.Equal(height, cursor);
    }

    [Fact]
    public void A_plain_case_gives_nine_pieces_that_tile_both_spans()
    {
        var slice = NineSliceGeometry.Compute(64, 64, SliceInsets.Uniform(12), 200, 100);

        AssertTiles(slice.Source, 64, 64, "源");
        AssertTiles(slice.Destination, 200, 100, "目标");

        // The corners keep their size; the middle takes the rest.
        Assert.Equal(new PixelRect(0, 0, 12, 12), slice.Destination[0]);
        Assert.Equal(new PixelRect(12, 12, 176, 76), slice.Destination[4]);
    }

    [Fact]
    public void No_insets_means_the_centre_covers_everything()
    {
        var slice = NineSliceGeometry.Compute(32, 32, SliceInsets.None, 128, 64);

        AssertTiles(slice.Destination, 128, 64, "目标");
        Assert.Equal(new PixelRect(0, 0, 128, 64), slice.Destination[4]);

        foreach (var index in new[] { 0, 1, 2, 3, 5, 6, 7, 8 })
        {
            Assert.Equal(0, slice.Destination[index].Width * slice.Destination[index].Height);
        }
    }

    [Fact]
    public void A_destination_smaller_than_its_corners_scales_them_instead_of_overlapping()
    {
        // The destination is 20 wide but the frame asks for 40 of border: without
        // scaling, the two corners would cross.
        var slice = NineSliceGeometry.Compute(128, 128, SliceInsets.Uniform(40), 20, 20);

        AssertTiles(slice.Destination, 20, 20, "目标");

        Assert.Equal(20, slice.Destination[0].Width + slice.Destination[2].Width);
        Assert.Equal(0, slice.Destination[1].Width);

        // The ratio is preserved rather than split evenly.
        Assert.Equal(10, slice.Destination[0].Width);
    }

    [Fact]
    public void Asymmetric_corners_keep_their_ratio_when_scaled()
    {
        var insets = new SliceInsets(Left: 30, Top: 10, Right: 10, Bottom: 10);

        var slice = NineSliceGeometry.Compute(100, 100, insets, 20, 20);

        AssertTiles(slice.Destination, 20, 20, "目标");

        // 30:10 scaled to 20 total is 15:5, not 10:10.
        Assert.Equal(15, slice.Destination[0].Width);
        Assert.Equal(5, slice.Destination[2].Width);
    }

    [Fact]
    public void Insets_larger_than_the_source_are_clamped_so_the_middle_never_goes_negative()
    {
        var slice = NineSliceGeometry.Compute(16, 16, SliceInsets.Uniform(50), 100, 100);

        AssertTiles(slice.Source, 16, 16, "源");
        AssertTiles(slice.Destination, 100, 100, "目标");

        Assert.Equal(0, slice.Source[4].Width);
        Assert.Equal(0, slice.Source[4].Height);
    }

    [Fact]
    public void A_zero_sized_destination_still_produces_nine_pieces()
    {
        var slice = NineSliceGeometry.Compute(64, 64, SliceInsets.Uniform(8), 0, 0);

        Assert.Equal(NineSlice.PieceCount, slice.Destination.Count);
        foreach (var piece in slice.Destination)
        {
            Assert.Equal(0, piece.Width * piece.Height);
        }
    }

    [Fact]
    public void A_wide_range_of_shapes_tiles_exactly()
    {
        // Swept, because off-by-one errors hide at particular remainders — exactly
        // where a hand-picked example would not look.
        foreach (var sourceSize in new[] { 1, 2, 3, 7, 16, 33, 64, 128 })
        {
            foreach (var inset in new[] { 0, 1, 2, 5, 16, 40, 200 })
            {
                foreach (var destination in new[] { 0, 1, 3, 17, 64, 199, 512 })
                {
                    var slice = NineSliceGeometry.Compute(
                        sourceSize,
                        sourceSize,
                        SliceInsets.Uniform(inset),
                        destination,
                        destination);

                    AssertTiles(slice.Source, sourceSize, sourceSize, $"源 {sourceSize} 内缩 {inset}");
                    AssertTiles(slice.Destination, destination, destination, $"目标 {destination} 内缩 {inset}");
                }
            }
        }
    }

    [Fact]
    public void Non_square_sources_and_destinations_are_handled_independently()
    {
        var slice = NineSliceGeometry.Compute(200, 40, new SliceInsets(20, 4, 20, 4), 640, 90);

        AssertTiles(slice.Source, 200, 40, "源");
        AssertTiles(slice.Destination, 640, 90, "目标");

        Assert.Equal(600, slice.Destination[4].Width);
        Assert.Equal(82, slice.Destination[4].Height);
    }

    [Fact]
    public void An_impossible_source_is_refused_rather_than_guessed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NineSliceGeometry.Compute(0, 10, SliceInsets.None, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => NineSliceGeometry.Compute(10, 0, SliceInsets.None, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => NineSliceGeometry.Compute(10, 10, SliceInsets.None, -1, 10));
    }

    [Fact]
    public void Negative_insets_are_treated_as_no_inset()
    {
        var slice = NineSliceGeometry.Compute(64, 64, new SliceInsets(-5, -5, -5, -5), 100, 100);

        AssertTiles(slice.Destination, 100, 100, "目标");
        Assert.Equal(new PixelRect(0, 0, 100, 100), slice.Destination[4]);
    }

    [Fact]
    public void The_piece_names_line_up_with_the_draw_order()
    {
        // The names are documentation for the renderer; a mismatch would silently
        // put a corner in the centre.
        Assert.Equal(9, NineSlice.Names.Length);
        Assert.Equal("topLeft", NineSlice.Names[0]);
        Assert.Equal("centre", NineSlice.Names[4]);
        Assert.Equal("bottomRight", NineSlice.Names[8]);

        var slice = NineSliceGeometry.Compute(64, 64, SliceInsets.Uniform(10), 100, 100);
        Assert.Equal(new PixelRect(0, 0, 10, 10), slice.Source[0]);
        Assert.Equal(new PixelRect(54, 54, 10, 10), slice.Source[8]);
    }
}
