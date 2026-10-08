using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Colour arithmetic, directly rather than through a button's resolved brush.
///
/// <para>
/// Mutation testing found this unguarded: widening the amount clamp to 2 changed
/// nothing the suite noticed, because every existing test mixed with an amount it
/// already knew was in range. The clamp is the only thing standing between a skin
/// author's "amount: 2" and a colour that wraps around the byte.
/// </para>
/// </summary>
public sealed class ColorMathTests
{
    [Fact]
    public void Mixing_all_the_way_toward_a_colour_gives_that_colour()
    {
        Assert.Equal("#FFFFFFFF", ColorMath.Mix("#FF000000", "#FFFFFFFF", 1.0));
    }

    [Fact]
    public void Mixing_by_nothing_leaves_the_colour_alone()
    {
        Assert.Equal("#FF123456", ColorMath.Mix("#FF123456", "#FFFFFFFF", 0.0));
    }

    [Fact]
    public void Mixing_halfway_lands_in_the_middle()
    {
        // 0 -> 255 at one half rounds away from zero to 128, and alpha lightens too.
        Assert.Equal("#FF808080", ColorMath.Mix("#FF000000", "#FFFFFFFF", 0.5));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(10.0)]
    [InlineData(1000.0)]
    public void An_amount_above_one_is_the_same_as_one(double amount)
    {
        // Without the clamp this over-blends and the byte cast wraps: a colour meant
        // to be white comes out as something near black.
        Assert.Equal("#FFFFFFFF", ColorMath.Mix("#FF000000", "#FFFFFFFF", amount));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-100.0)]
    public void A_negative_amount_is_the_same_as_none(double amount)
    {
        Assert.Equal("#FF123456", ColorMath.Mix("#FF123456", "#FFFFFFFF", amount));
    }

    [Fact]
    public void An_huge_amount_never_produces_a_wrapped_colour()
    {
        // The specific failure the clamp prevents: 255 + overshoot, cast to a byte,
        // arriving back at a dark colour.
        var mixed = ColorMath.Mix("#FF000000", "#FFFFFFFF", 100);

        Assert.Equal(255, Convert.ToByte(mixed.Substring(3, 2), 16));
        Assert.Equal(255, Convert.ToByte(mixed.Substring(5, 2), 16));
        Assert.Equal(255, Convert.ToByte(mixed.Substring(7, 2), 16));
    }

    [Fact]
    public void An_unreadable_colour_is_passed_through_not_replaced()
    {
        // Returning the target instead would hide the skin author's mistake behind a
        // value they never wrote.
        Assert.Equal("not-a-colour", ColorMath.Mix("not-a-colour", "#FFFFFFFF", 0.5));
        Assert.Equal("#FF000000", ColorMath.Mix("#FF000000", "not-a-colour", 0.5));
    }

    [Fact]
    public void Lighten_and_darken_move_in_the_directions_they_are_named_for()
    {
        Assert.Equal("#FFFFFFFF", ColorMath.Lighten("#FF000000", 1.0));
        Assert.Equal("#FF000000", ColorMath.Darken("#FFFFFFFF", 1.0));

        // Neither is a no-op.
        Assert.NotEqual("#FF404040", ColorMath.Lighten("#FF404040", 0.5));
        Assert.NotEqual("#FF404040", ColorMath.Darken("#FF404040", 0.5));
    }

    [Fact]
    public void Hex_output_keeps_the_alpha_channel()
    {
        Assert.Equal("#FF123456", ColorMath.ToHex((0xFF, 0x12, 0x34, 0x56)));
        Assert.Equal("#00123456", ColorMath.ToHex((0x00, 0x12, 0x34, 0x56)));
    }

    [Fact]
    public void The_three_documented_forms_each_parse_to_the_right_channels()
    {
        Assert.True(ColorMath.TryParse("#AABBCCDD", out var longForm));
        Assert.Equal(((byte)0xAA, (byte)0xBB, (byte)0xCC, (byte)0xDD), longForm);

        // #RRGGBB is opaque: the alpha is filled in, not left at whatever the
        // initialiser happened to be.
        Assert.True(ColorMath.TryParse("#123456", out var shortAlpha));
        Assert.Equal(((byte)0xFF, (byte)0x12, (byte)0x34, (byte)0x56), shortAlpha);
    }

    [Fact]
    public void The_shorthand_expands_each_digit_rather_than_rotating_them()
    {
        // "#abc" means "#aabbcc": the classic way to get this wrong is to slide the
        // digits instead of doubling each one.
        Assert.True(ColorMath.TryParse("#abc", out var shorthand));
        Assert.Equal(((byte)0xFF, (byte)0xAA, (byte)0xBB, (byte)0xCC), shorthand);
    }

    [Theory]
    [InlineData("#ARGB")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    public void A_length_that_is_not_documented_is_refused_rather_than_guessed(string value)
    {
        // Four-digit shorthand is a real CSS form and this deliberately does not take
        // it: half-parsing a form nobody declared is how a colour becomes a surprise.
        Assert.False(ColorMath.TryParse(value, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-colour")]
    [InlineData("#GGGGGG")]
    public void Something_that_is_not_a_colour_is_reported_as_such(string? value)
    {
        Assert.False(ColorMath.TryParse(value, out _));
    }
}
