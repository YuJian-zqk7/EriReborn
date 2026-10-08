namespace EriReborn.Platform.Abstractions;

/// <summary>
/// How much of the screen the system has taken: the status bar above, the gesture
/// bar below, and whatever a display cutout claims on either side.
///
/// <para>
/// Content placed under those areas is content the user cannot reach. The values
/// come from the platform, which means they can also be wrong — so they are
/// sanitised before they reach a layout rather than trusted.
/// </para>
/// </summary>
public sealed record SafeAreaInsets(double Top, double Right, double Bottom, double Left)
{
    /// <summary>
    /// No real device reserves more than a fraction of the screen. Anything larger
    /// is a broken platform call, and honouring it would leave the user looking at
    /// a blank page with no way to tell why.
    /// </summary>
    public const double MaxPlausible = 200;

    public static readonly SafeAreaInsets None = new(0, 0, 0, 0);

    /// <summary>
    /// Converts pixel insets into the layout units a UI actually uses.
    ///
    /// <para>
    /// Android reports insets in pixels; Avalonia lays out in density-independent
    /// units. Skipping the division makes the padding four times too large on a
    /// 4x screen — which looks like a layout bug, not a units bug, and is why this
    /// conversion is a function of its own rather than an expression at the call
    /// site.
    /// </para>
    /// </summary>
    /// <param name="density">Pixels per layout unit; a nonsense value is treated as 1.</param>
    public static SafeAreaInsets FromPixels(double top, double right, double bottom, double left, double density)
    {
        // A zero or absurd density is a broken platform answer; scaling by it would
        // turn a small inset into an enormous one, so it is ignored instead.
        var scale = density is > 0 and <= 10 ? density : 1;

        return new SafeAreaInsets(top / scale, right / scale, bottom / scale, left / scale).Sanitized();
    }

    public bool IsEmpty => Top <= 0 && Right <= 0 && Bottom <= 0 && Left <= 0;

    /// <summary>True when every edge is a value a real device could report.</summary>
    public bool IsPlausible =>
        Top is >= 0 and <= MaxPlausible
        && Right is >= 0 and <= MaxPlausible
        && Bottom is >= 0 and <= MaxPlausible
        && Left is >= 0 and <= MaxPlausible;

    /// <summary>
    /// The values as a layout should see them: negatives become zero and absurd
    /// values are capped.
    ///
    /// <para>
    /// A negative padding is an error in most layout systems, and a huge one is
    /// indistinguishable from a blank window. Neither is worth passing on just
    /// because the platform said so.
    /// </para>
    /// </summary>
    public SafeAreaInsets Sanitized() => new(
        Clamp(Top),
        Clamp(Right),
        Clamp(Bottom),
        Clamp(Left));

    private static double Clamp(double value)
        => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, MaxPlausible);

    public override string ToString() => $"T{Top:0.#} R{Right:0.#} B{Bottom:0.#} L{Left:0.#}";
}

/// <summary>Where the current safe area comes from.</summary>
public interface ISafeAreaService
{
    SafeAreaInsets Current { get; }

    /// <summary>Raised when the system hands the space back or takes more — a rotation, a cutout.</summary>
    event EventHandler<SafeAreaInsets>? Changed;
}

/// <summary>
/// A platform with no system insets to report.
///
/// <para>
/// This is not a placeholder that pretends to work: a decorated desktop window's
/// client area really is the whole window, so zero is the correct answer. It is
/// named for what it says rather than for what it lacks.
/// </para>
/// </summary>
public sealed class NoInsetsSafeAreaService : ISafeAreaService
{
    public SafeAreaInsets Current => SafeAreaInsets.None;

    public event EventHandler<SafeAreaInsets>? Changed;

    /// <summary>Kept so the event is not an empty promise in the type, and never raised.</summary>
    internal void Raise(SafeAreaInsets insets) => Changed?.Invoke(this, insets);
}
