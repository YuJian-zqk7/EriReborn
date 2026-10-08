using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A slider is three parts — track, filled portion, thumb — and each needs its
/// own value. A theme that only colours "the slider" produces the slider that
/// looks like it belongs to a different program (spec 110).
/// </summary>
public sealed class SliderResourceTests
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

    [Fact]
    public void Every_shipped_skin_resolves_all_five_slider_values()
    {
        foreach (var skin in ShippedSkins())
        {
            var values = SkinValueSet.FromManifest(skin);

            Assert.True(values.SliderTrackHeight > 0, $"{skin.Id} 的轨道高度非正。");
            Assert.True(values.SliderThumbSize > 0, $"{skin.Id} 的滑块尺寸非正。");

            foreach (var (name, colour) in new[]
                     {
                         ("sliderTrack", values.SliderTrackColor),
                         ("sliderFill", values.SliderFillColor),
                         ("sliderThumb", values.SliderThumbColor),
                     })
            {
                Assert.True(ColorMath.TryParse(colour, out _), $"{skin.Id} 的 {name} 不是颜色：'{colour}'");
            }
        }
    }

    [Fact]
    public void A_touch_skin_gets_a_larger_thumb_than_a_mouse_one()
    {
        var touch = SkinValueSet.FromManifest(ShippedSkins().Single(skin => skin.Id == "eri_android"));
        var mouse = SkinValueSet.FromManifest(ShippedSkins().Single(skin => skin.Id == "eri_windows"));

        // A 18px thumb is not grabbable with a finger.
        Assert.True(touch.SliderThumbSize > mouse.SliderThumbSize);
    }

    [Fact]
    public void The_three_parts_are_not_the_same_colour()
    {
        // One colour for all three is the thing this is meant to prevent: the thumb
        // would vanish into the filled portion.
        foreach (var skin in ShippedSkins())
        {
            var values = SkinValueSet.FromManifest(skin);

            Assert.NotEqual(
                values.SliderFillColor.ToUpperInvariant(),
                values.SliderThumbColor.ToUpperInvariant());
        }
    }

    [Fact]
    public void An_explicit_slider_value_wins_over_the_derived_one()
    {
        var manifest = new SkinManifest
        {
            Id = "custom",
            Name = "Custom",
            Colors = new Dictionary<string, string>
            {
                ["sliderTrack"] = "#112233",
                ["sliderFill"] = "#445566",
                ["sliderThumb"] = "#778899",
            },
            Controls = new Dictionary<string, string>
            {
                ["sliderTrackHeight"] = "9",
                ["sliderThumbSize"] = "24",
            },
        };

        var values = SkinValueSet.FromManifest(manifest);

        Assert.Equal("#112233", values.SliderTrackColor);
        Assert.Equal("9", values.SliderTrackHeight.ToString("0"));
        Assert.Equal(24, values.SliderThumbSize);
    }

    [Fact]
    public void The_styles_address_each_part_separately()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "EriReborn.UI.Avalonia", "Styles", "Skin.axaml"));

        foreach (var selector in new[]
                 {
                     "Selector=\"Slider\"",
                     "Selector=\"Slider /template/ Track\"",
                     "Selector=\"Slider /template/ Thumb\"",
                     "Selector=\"Slider:disabled\"",
                 })
        {
            Assert.Contains(selector, xaml, StringComparison.Ordinal);
        }

        // And every projected value is actually bound, or it is a colour nobody sees.
        foreach (var key in new[]
                 {
                     "SkinSliderTrackHeight", "SkinSliderThumbSize",
                     "SkinSliderTrackBrush", "SkinSliderFillBrush", "SkinSliderThumbBrush",
                 })
        {
            Assert.Contains(key, xaml, StringComparison.Ordinal);
        }
    }
}
