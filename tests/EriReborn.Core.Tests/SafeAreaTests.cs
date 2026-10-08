using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Safe-area values come from the platform, which means they can also be wrong.
/// What reaches a layout is sanitised first: a negative padding is an error in
/// most layout systems, and an enormous one is indistinguishable from a blank
/// window (spec 118).
/// </summary>
public sealed class SafeAreaTests
{
    [Fact]
    public void Nothing_reserved_is_empty()
    {
        Assert.True(SafeAreaInsets.None.IsEmpty);
        Assert.True(SafeAreaInsets.None.IsPlausible);
        Assert.Equal(SafeAreaInsets.None, SafeAreaInsets.None.Sanitized());
    }

    [Fact]
    public void A_real_phone_answer_is_left_alone()
    {
        var insets = new SafeAreaInsets(24, 0, 48, 0).Sanitized();

        Assert.Equal(24, insets.Top);
        Assert.Equal(48, insets.Bottom);
        Assert.False(insets.IsEmpty);
    }

    [Fact]
    public void Negative_insets_become_zero()
    {
        // A platform reporting a negative inset would produce a negative padding,
        // and a negative padding is a layout error rather than a small one.
        var insets = new SafeAreaInsets(-30, -1, -5, -100).Sanitized();

        Assert.Equal(SafeAreaInsets.None, insets);
    }

    [Fact]
    public void An_absurd_inset_is_capped_rather_than_honoured()
    {
        var insets = new SafeAreaInsets(100_000, 0, double.MaxValue, 0).Sanitized();

        Assert.Equal(SafeAreaInsets.MaxPlausible, insets.Top);
        Assert.Equal(SafeAreaInsets.MaxPlausible, insets.Bottom);
        Assert.True(insets.IsPlausible);
    }

    [Fact]
    public void Not_a_number_is_treated_as_nothing()
    {
        var insets = new SafeAreaInsets(double.NaN, 0, 0, 0).Sanitized();

        Assert.Equal(0, insets.Top);
    }

    [Fact]
    public void Plausibility_is_what_it_says()
    {
        Assert.True(new SafeAreaInsets(0, 0, 0, 0).IsPlausible);
        Assert.True(new SafeAreaInsets(SafeAreaInsets.MaxPlausible, 0, 0, 0).IsPlausible);
        Assert.False(new SafeAreaInsets(SafeAreaInsets.MaxPlausible + 1, 0, 0, 0).IsPlausible);
        Assert.False(new SafeAreaInsets(-1, 0, 0, 0).IsPlausible);
    }

    // ------------------------------------------------- pixels to layout units

    [Fact]
    public void Pixel_insets_are_converted_to_layout_units()
    {
        // Android reports pixels; the layout works in density-independent units.
        // Skipping the division makes the padding three times too big on a 3x screen,
        // which reads as a layout bug rather than a units bug.
        var insets = SafeAreaInsets.FromPixels(top: 72, right: 0, bottom: 144, left: 0, density: 3);

        Assert.Equal(24, insets.Top);
        Assert.Equal(48, insets.Bottom);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(1000)]
    [InlineData(double.NaN)]
    public void A_nonsense_density_does_not_scale_the_insets(double density)
    {
        // Scaling by a broken density turns a small inset into an enormous one, so
        // it is ignored instead of trusted.
        var insets = SafeAreaInsets.FromPixels(50, 0, 0, 0, density);

        Assert.Equal(50, insets.Top);
    }

    [Fact]
    public void Pixel_insets_are_sanitised_too()
    {
        var insets = SafeAreaInsets.FromPixels(-100, 0, 10_000_000, 0, density: 1);

        Assert.Equal(0, insets.Top);
        Assert.Equal(SafeAreaInsets.MaxPlausible, insets.Bottom);
    }

    [Fact]
    public void A_desktop_platform_reserves_nothing()
    {
        // Not a placeholder pretending to work: a decorated desktop window's client
        // area really is the whole window.
        var service = new NoInsetsSafeAreaService();

        Assert.True(service.Current.IsEmpty);
        Assert.Equal(SafeAreaInsets.None, service.Current);
    }
}
