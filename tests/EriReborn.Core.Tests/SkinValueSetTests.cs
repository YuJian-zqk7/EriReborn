using EriReborn.App.Shared;
using EriReborn.Core.Logging;
using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A skin must actually drive the UI (spec 40/47). These tests assert against
/// the six shipped manifests, so a manifest edit that the code ignores — or a
/// mapping that drifts from the data — fails here.
/// </summary>
public sealed class SkinValueSetTests
{
    private static async Task<IReadOnlyList<SkinManifest>> ShippedSkinsAsync()
    {
        var paths = AppPaths.Detect(userDataOverride: Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N")));
        var engine = new SkinEngine(AppLog.For("Test"));
        return await engine.DiscoverAsync(paths.SkinsDirectory);
    }

    private static SkinManifest Skin(IReadOnlyList<SkinManifest> skins, string id)
        => skins.Single(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    [Fact]
    public async Task Every_shipped_skin_produces_a_complete_value_set()
    {
        var skins = await ShippedSkinsAsync();
        Assert.Equal(6, skins.Count);

        foreach (var skin in skins)
        {
            var values = SkinValueSet.FromManifest(skin);

            Assert.Equal(skin.Id, values.SkinId);
            Assert.Equal(SkinValueSet.ColorKeys.Length, values.Colors.Count);
            Assert.False(string.IsNullOrWhiteSpace(values.FontFamily));
            Assert.True(values.FontSize > 0);
            Assert.True(values.FontSizeLarge >= values.FontSize);
            Assert.True(values.FontSizeSmall <= values.FontSize);
            Assert.True(values.CornerRadius > 0);
            Assert.True(values.ButtonHeight > 0);
            Assert.True(values.InputHeight > 0);
            Assert.True(values.ListRowHeight > 0);
            Assert.True(values.NavItemHeight > 0);
            Assert.True(values.WindowMinWidth > 0);
            Assert.True(values.WindowMinHeight > 0);
        }
    }

    [Fact]
    public async Task Mobile_skins_require_finger_sized_targets_and_desktop_skins_do_not()
    {
        foreach (var skin in await ShippedSkinsAsync())
        {
            var values = SkinValueSet.FromManifest(skin);

            if (skin.IsMobile)
            {
                Assert.True(values.TouchTarget, $"{skin.Id} should declare a touch target");
                Assert.True(values.MinTargetHeight >= 48, $"{skin.Id} target {values.MinTargetHeight} < 48");
                Assert.Equal(56, values.ListRowHeight);
                Assert.Equal(360, values.WindowMinWidth);
            }
            else
            {
                Assert.False(values.TouchTarget, $"{skin.Id} should not declare a touch target");
                Assert.Equal(values.ButtonHeight, values.MinTargetHeight);
                Assert.Equal(960, values.WindowMinWidth);
            }
        }
    }

    [Fact]
    public async Task Density_and_corner_radius_come_from_the_manifest_not_from_defaults()
    {
        var skins = await ShippedSkinsAsync();

        var techWindows = SkinValueSet.FromManifest(Skin(skins, "tech_windows"));
        Assert.Equal("compact", techWindows.Density);
        Assert.Equal(4, techWindows.Spacing);
        Assert.Equal(6, techWindows.CornerRadius);
        Assert.Equal(13, techWindows.FontSize);

        var androidStyle = SkinValueSet.FromManifest(Skin(skins, "android_style"));
        Assert.Equal(16, androidStyle.CornerRadius);
        Assert.Equal(8, androidStyle.CornerRadiusSmall);

        var eriWindows = SkinValueSet.FromManifest(Skin(skins, "eri_windows"));
        Assert.Equal("comfortable", eriWindows.Density);
        Assert.Equal(8, eriWindows.Spacing);

        // The dialog frame art's own corner measures ~29 DIP at dialog size, so the card
        // inside it needs a matching curve; the old 10 read as a square block in a round
        // frame. Other skins keep their own geometry on purpose.
        Assert.Equal(22, eriWindows.CornerRadius);
    }

    [Fact]
    public async Task The_six_skins_are_visually_distinguishable()
    {
        var values = (await ShippedSkinsAsync()).Select(SkinValueSet.FromManifest).ToList();

        // Distinct accents and distinct geometry, not six recolours of one layout.
        Assert.True(values.Select(v => v.Colors["accent"]).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 4);
        Assert.True(values.Select(v => v.CornerRadius).Distinct().Count() >= 4);
        Assert.True(values.Select(v => v.FontSize).Distinct().Count() >= 2);
        Assert.True(values.Select(v => v.ListRowHeight).Distinct().Count() >= 2);
    }

    [Fact]
    public async Task Navigation_and_window_rules_are_carried_through()
    {
        var skins = await ShippedSkinsAsync();

        foreach (var skin in skins)
        {
            var values = SkinValueSet.FromManifest(skin);
            Assert.False(string.IsNullOrWhiteSpace(values.NavMode));
            Assert.False(string.IsNullOrWhiteSpace(values.NavPosition));
            Assert.False(string.IsNullOrWhiteSpace(values.WindowChrome));
            Assert.True(values.WindowMinHeight > 0);
        }
    }

    [Fact]
    public void A_sparse_manifest_falls_back_to_documented_defaults()
    {
        var bare = new SkinManifest { Id = "bare", Name = "Bare" };

        var values = SkinValueSet.FromManifest(bare);

        Assert.Empty(values.Colors);
        Assert.Equal("Segoe UI", values.FontFamily);
        Assert.Equal(14, values.FontSize);
        Assert.Equal(18, values.FontSizeLarge);
        Assert.Equal(12, values.FontSizeSmall);
        Assert.Equal(8, values.CornerRadius);
        Assert.Equal(32, values.ButtonHeight);
        Assert.Equal(36, values.ListRowHeight);
        Assert.Equal(40, values.NavItemHeight);
        Assert.Equal(960, values.WindowMinWidth);
        Assert.Equal(600, values.WindowMinHeight);
        Assert.Equal("sidebar", values.NavMode);
    }

    [Fact]
    public void Non_numeric_manifest_values_do_not_break_the_mapping()
    {
        var sloppy = new SkinManifest
        {
            Id = "sloppy",
            Name = "Sloppy",
            Metrics = new Dictionary<string, string> { ["cornerRadius"] = "round", ["density"] = "   " },
            Controls = new Dictionary<string, string> { ["buttonHeight"] = "big", ["touchTarget"] = "yes" },
            Window = new Dictionary<string, string> { ["minwidth"] = "wide" },
        };

        var values = SkinValueSet.FromManifest(sloppy);

        Assert.Equal(8, values.CornerRadius);
        Assert.Equal(32, values.ButtonHeight);
        Assert.Equal(960, values.WindowMinWidth);
        Assert.False(values.TouchTarget);
        Assert.Equal("comfortable", values.Density);
    }
}
