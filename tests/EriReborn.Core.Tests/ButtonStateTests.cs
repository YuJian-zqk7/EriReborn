using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A control with a rest state and nothing else looks unfinished the moment the
/// pointer touches it. Every shipped skin must therefore provide hover, pressed
/// and disabled — declared if the author wants, derived if not (spec 109).
/// </summary>
public sealed class ButtonStateTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "skins")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private static IEnumerable<SkinManifest> ShippedSkins()
        => Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "assets", "skins"), "skin.json", SearchOption.AllDirectories)
            .Select(path => SkinManifestParser.Parse(File.ReadAllText(path), path));

    // ------------------------------------------------------------ colour maths

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#ffffff")]
    [InlineData("FFFFFF")]
    [InlineData("#FFF")]
    [InlineData("#FFFFFFFF")]
    public void A_colour_is_read_in_any_of_the_usual_forms(string value)
    {
        Assert.True(ColorMath.TryParse(value, out var color));
        Assert.Equal(255, color.R);
        Assert.Equal(255, color.G);
        Assert.Equal(255, color.B);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#GGGGGG")]
    [InlineData("#FF")]
    [InlineData("rgb(1,2,3)")]
    public void An_unreadable_colour_is_refused_rather_than_guessed(string? value)
    {
        Assert.False(ColorMath.TryParse(value, out _));
    }

    [Fact]
    public void Short_hex_is_expanded_the_way_css_does_it()
    {
        Assert.True(ColorMath.TryParse("#abc", out var color));
        Assert.Equal(0xAA, color.R);
        Assert.Equal(0xBB, color.G);
        Assert.Equal(0xCC, color.B);
    }

    [Fact]
    public void Lightening_and_darkening_move_in_the_expected_direction()
    {
        // Amount is how far to move, so 1.0 is all the way.
        Assert.Equal("#FFFFFFFF", ColorMath.Lighten("#FF404040", 1.0));
        Assert.Equal("#FF000000", ColorMath.Darken("#FF404040", 1.0));

        // Half way from #000000 to #FFFFFF is #808080 by rounding, not truncation.
        Assert.Equal("#FF808080", ColorMath.Mix("#FF000000", "#FFFFFFFF", 0.5));

        // Half way from #404040 to white is 64 + 95.5 = 159.5, rounded to 0xA0.
        Assert.Equal("#FFA0A0A0", ColorMath.Lighten("#FF404040", 0.5));
    }

    [Fact]
    public void Mixing_clamps_instead_of_wrapping()
    {
        Assert.Equal("#FFFFFFFF", ColorMath.Mix("#FFFFFFFF", "#FFFFFFFF", 5));
        Assert.Equal("#FF000000", ColorMath.Mix("#FF000000", "#FFFFFFFF", -3));
    }

    [Fact]
    public void An_unreadable_colour_comes_back_untouched()
    {
        // Inventing a colour would hide the skin author's mistake behind a value
        // they never wrote.
        Assert.Equal("not-a-colour", ColorMath.Mix("not-a-colour", "#FFFFFFFF", 0.5));
    }

    // --------------------------------------------------------- derived states

    [Fact]
    public void Every_shipped_skin_resolves_all_four_state_colours()
    {
        var skins = ShippedSkins().ToList();
        Assert.NotEmpty(skins);

        foreach (var skin in skins)
        {
            var values = SkinValueSet.FromManifest(skin);

            foreach (var (name, colour) in new[]
                     {
                         ("accentHover", values.AccentHover),
                         ("accentPressed", values.AccentPressed),
                         ("surfaceDisabled", values.SurfaceDisabled),
                         ("foregroundDisabled", values.ForegroundDisabled),
                     })
            {
                Assert.True(
                    ColorMath.TryParse(colour, out _),
                    $"{skin.Id} 的 {name} 不是颜色：'{colour}'");
            }
        }
    }

    [Fact]
    public void Hover_differs_from_rest_and_pressed_differs_from_hover()
    {
        // A hover that equals rest is not a hover.
        foreach (var skin in ShippedSkins())
        {
            var values = SkinValueSet.FromManifest(skin);
            var accent = skin.GetColor("accent") ?? "#5B8DEF";

            Assert.NotEqual(accent.ToUpperInvariant(), values.AccentHover.ToUpperInvariant());
            Assert.NotEqual(values.AccentHover.ToUpperInvariant(), values.AccentPressed.ToUpperInvariant());
        }
    }

    [Fact]
    public void An_explicit_state_colour_wins_over_the_derived_one()
    {
        var manifest = new SkinManifest
        {
            Id = "custom",
            Name = "Custom",
            Colors = new Dictionary<string, string>
            {
                ["accent"] = "#FF0000",
                ["accentHover"] = "#00FF00",
                ["accentPressed"] = "#0000FF",
            },
        };

        var values = SkinValueSet.FromManifest(manifest);

        Assert.Equal("#00FF00", values.AccentHover);
        Assert.Equal("#0000FF", values.AccentPressed);
    }

    [Fact]
    public void A_light_skin_derives_a_darker_hover_and_a_dark_skin_a_lighter_one()
    {
        static SkinValueSet For(string theme) => SkinValueSet.FromManifest(new SkinManifest
        {
            Id = theme,
            Name = theme,
            Theme = theme,
            Colors = new Dictionary<string, string> { ["accent"] = "#FF808080" },
        });

        // Deriving in the wrong direction produces a hover that vanishes into the
        // background on one theme or the other.
        var dark = For("dark");
        var light = For("light");

        Assert.NotEqual(dark.AccentHover, light.AccentHover);

        Assert.True(ColorMath.TryParse(dark.AccentHover, out var darkHover));
        Assert.True(ColorMath.TryParse(light.AccentHover, out var lightHover));

        Assert.True(darkHover.R > 0x80, "深色皮肤的高亮应更亮。");
        Assert.True(lightHover.R < 0x80, "浅色皮肤的高亮应更暗。");
    }

    [Fact]
    public void A_skin_without_an_accent_still_gets_states()
    {
        var values = SkinValueSet.FromManifest(new SkinManifest { Id = "bare", Name = "Bare" });

        Assert.True(ColorMath.TryParse(values.AccentHover, out _));
        Assert.True(ColorMath.TryParse(values.AccentPressed, out _));
        Assert.True(ColorMath.TryParse(values.SurfaceDisabled, out _));
        Assert.True(ColorMath.TryParse(values.ForegroundDisabled, out _));
    }

    // ------------------------------------------------------------ the styles

    [Fact]
    public void The_button_styles_define_all_five_states()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "EriReborn.UI.Avalonia", "Styles", "Skin.axaml"));

        foreach (var selector in new[]
                 {
                     "Button",
                     "Button:pointerover",
                     "Button:pressed",
                     "Button:disabled",
                     "Button:focus",
                 })
        {
            Assert.Contains($"Selector=\"{selector}", xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_derived_state_colours_reach_the_styles()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "EriReborn.UI.Avalonia", "Styles", "Skin.axaml"));

        // A derived colour nothing binds to is the same as no colour at all.
        foreach (var key in new[] { "SkinAccentHover", "SkinAccentPressed", "SkinSurfaceDisabled", "SkinForegroundDisabled" })
        {
            Assert.Contains(key, xaml, StringComparison.Ordinal);
        }
    }
}
