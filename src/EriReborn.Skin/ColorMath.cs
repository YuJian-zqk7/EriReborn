using System.Globalization;

namespace EriReborn.Skin;

/// <summary>
/// Colour arithmetic for skin states.
///
/// <para>
/// A skin declares one accent, but a button needs at least three: rest, hover and
/// pressed. Asking every skin author to hand-pick all of them invites skins that
/// only define the easy one and inherit whatever the theme happened to ship. So
/// the others are derived — deterministically, and overridable when a skin does
/// want to be specific.
/// </para>
/// </summary>
public static class ColorMath
{
    /// <summary>Parses #RGB, #RRGGBB and #AARRGGBB. Anything else is refused, not guessed.</summary>
    public static bool TryParse(string? value, out (byte A, byte R, byte G, byte B) color)
    {
        color = (255, 0, 0, 0);

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length == 3)
        {
            // #abc means #aabbcc.
            text = string.Concat(text.Select(c => new string(c, 2)));
        }

        if (text.Length == 6)
        {
            text = "FF" + text;
        }

        if (text.Length != 8 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return false;
        }

        color = (
            (byte)((packed >> 24) & 0xFF),
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));

        return true;
    }

    public static string ToHex((byte A, byte R, byte G, byte B) color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>Moves a colour toward white. <paramref name="amount"/> is 0..1.</summary>
    public static string Lighten(string value, double amount)
        => Mix(value, "#FFFFFFFF", amount);

    /// <summary>Moves a colour toward black. <paramref name="amount"/> is 0..1.</summary>
    public static string Darken(string value, double amount)
        => Mix(value, "#FF000000", amount);

    /// <summary>
    /// Blends two colours. <paramref name="amount"/> is how much of
    /// <paramref name="toward"/> ends up in the result.
    /// </summary>
    public static string Mix(string value, string toward, double amount)
    {
        if (!TryParse(value, out var from) || !TryParse(toward, out var to))
        {
            // An unreadable colour is returned untouched: inventing one would hide
            // the skin author's mistake behind a value they never wrote.
            return value;
        }

        var t = Math.Clamp(amount, 0, 1);

        return ToHex((
            Blend(from.A, to.A, t),
            Blend(from.R, to.R, t),
            Blend(from.G, to.G, t),
            Blend(from.B, to.B, t)));
    }

    private static byte Blend(byte from, byte to, double t)
        => (byte)Math.Clamp(Math.Round(from + ((to - from) * t), MidpointRounding.AwayFromZero), 0, 255);
}
