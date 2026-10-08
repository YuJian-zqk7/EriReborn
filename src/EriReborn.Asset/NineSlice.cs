namespace EriReborn.Asset;

/// <summary>How much of each edge is a fixed border rather than stretchable middle.</summary>
public readonly record struct SliceInsets(int Left, int Top, int Right, int Bottom)
{
    public static readonly SliceInsets None = new(0, 0, 0, 0);

    /// <summary>The same inset on every edge.</summary>
    public static SliceInsets Uniform(int value) => new(value, value, value, value);
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>
/// Where each of the nine pieces is read from and drawn to, in draw order:
/// top-left, top-centre, top-right, middle-left, centre, middle-right,
/// bottom-left, bottom-centre, bottom-right.
/// </summary>
public sealed record NineSlice(
    IReadOnlyList<PixelRect> Source,
    IReadOnlyList<PixelRect> Destination)
{
    public const int PieceCount = 9;

    public static readonly string[] Names =
    {
        "topLeft", "topCentre", "topRight",
        "middleLeft", "centre", "middleRight",
        "bottomLeft", "bottomCentre", "bottomRight",
    };
}

/// <summary>
/// Computes nine-slice rectangles.
///
/// <para>
/// Pure arithmetic, because the failure is visual and quiet: overlapping or
/// gapped pieces produce a frame that looks almost right. The two cases that
/// actually break implementations are here on purpose — insets larger than the
/// source, and a destination smaller than its own corners. Both are handled by
/// scaling the border down instead of letting the pieces collide.
/// </para>
/// </summary>
public static class NineSliceGeometry
{
    public static NineSlice Compute(
        int sourceWidth,
        int sourceHeight,
        SliceInsets insets,
        int destinationWidth,
        int destinationHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "源尺寸必须为正。");
        }

        if (destinationWidth < 0 || destinationHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationWidth), "目标尺寸不能为负。");
        }

        var (left, right) = Fit(insets.Left, insets.Right, sourceWidth);
        var (top, bottom) = Fit(insets.Top, insets.Bottom, sourceHeight);

        // The source columns and rows, in pixels.
        var sourceX = new[] { 0, left, sourceWidth - right };
        var sourceW = new[] { left, sourceWidth - left - right, right };
        var sourceY = new[] { 0, top, sourceHeight - bottom };
        var sourceH = new[] { top, sourceHeight - top - bottom, bottom };

        // The destination borders, scaled down when they would not fit.
        var (destinationLeft, destinationRight) = Fit(left, right, destinationWidth);
        var (destinationTop, destinationBottom) = Fit(top, bottom, destinationHeight);

        var destinationX = new[] { 0, destinationLeft, destinationWidth - destinationRight };
        var destinationW = new[] { destinationLeft, destinationWidth - destinationLeft - destinationRight, destinationRight };
        var destinationY = new[] { 0, destinationTop, destinationHeight - destinationBottom };
        var destinationH = new[] { destinationTop, destinationHeight - destinationTop - destinationBottom, destinationBottom };

        var source = new PixelRect[NineSlice.PieceCount];
        var destination = new PixelRect[NineSlice.PieceCount];

        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var index = (row * 3) + column;
                source[index] = new PixelRect(sourceX[column], sourceY[row], sourceW[column], sourceH[row]);
                destination[index] = new PixelRect(destinationX[column], destinationY[row], destinationW[column], destinationH[row]);
            }
        }

        return new NineSlice(source, destination);
    }

    /// <summary>
    /// Clamps a pair of opposite insets so together they never exceed the span,
    /// keeping their ratio. Splitting the span evenly would distort a frame whose
    /// edges differ, which is exactly what a sliced asset looks like.
    /// </summary>
    private static (int First, int Second) Fit(int first, int second, int span)
    {
        var a = Math.Max(0, first);
        var b = Math.Max(0, second);

        if (span <= 0)
        {
            return (0, 0);
        }

        if (a + b <= span)
        {
            return (a, b);
        }

        if (a == 0 && b == 0)
        {
            return (0, 0);
        }

        var scaledFirst = (int)Math.Round((double)a * span / (a + b), MidpointRounding.AwayFromZero);
        scaledFirst = Math.Clamp(scaledFirst, 0, span);

        // The second takes whatever is left, so the pair always sums to the span.
        return (scaledFirst, span - scaledFirst);
    }
}
