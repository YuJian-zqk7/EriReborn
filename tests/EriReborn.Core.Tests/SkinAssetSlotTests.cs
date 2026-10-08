using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Slots, not free-form keys: the UI asks for "the companion", and a skin that
/// declares none has none. Eri's companion is 小黑, the black cat that follows her,
/// and it is deliberately optional — a skin without cat art must not be given a
/// stand-in that looks like one.
/// </summary>
public sealed class SkinAssetSlotTests
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

    private static IEnumerable<SkinManifest> Shipped()
        => Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "assets", "skins"), "skin.json", SearchOption.AllDirectories)
            .Select(path => SkinManifestParser.Parse(File.ReadAllText(path), path));

    [Fact]
    public void The_slots_are_named_and_few()
    {
        Assert.Equal(new[] { "character", "companion", "logo", "emptyState", "loadingState" }, SkinAssets.Slots);
    }

    [Fact]
    public void A_declared_slot_resolves_to_its_asset_id()
    {
        var skin = new SkinManifest
        {
            Id = "demo",
            Name = "Demo",
            Assets = new Dictionary<string, string>
            {
                ["character"] = "demo_character",
                ["companion"] = "demo_xiaohei",
            },
        };

        Assert.Equal("demo_character", SkinAssets.IdFor(skin, SkinAssets.Character));
        Assert.Equal("demo_xiaohei", SkinAssets.IdFor(skin, SkinAssets.Companion));
    }

    [Fact]
    public void An_undeclared_slot_is_null_rather_than_a_guess()
    {
        var skin = new SkinManifest { Id = "demo", Name = "Demo" };

        // A stand-in here would put one skin's cat on another skin's page.
        Assert.Null(SkinAssets.IdFor(skin, SkinAssets.Companion));
        Assert.Null(SkinAssets.IdFor(skin, SkinAssets.Character));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_declaration_counts_as_none(string value)
    {
        var skin = new SkinManifest
        {
            Id = "demo",
            Name = "Demo",
            Assets = new Dictionary<string, string> { ["companion"] = value },
        };

        Assert.Null(SkinAssets.IdFor(skin, SkinAssets.Companion));
    }

    [Fact]
    public void No_skin_is_refused_the_lookup()
    {
        Assert.Null(SkinAssets.IdFor(null, SkinAssets.Companion));
        Assert.Empty(SkinAssets.DeclaredSlots(null));
    }

    [Fact]
    public void Every_shipped_skin_declares_a_character()
    {
        var skins = Shipped().ToList();
        Assert.NotEmpty(skins);

        foreach (var skin in skins)
        {
            Assert.NotNull(SkinAssets.IdFor(skin, SkinAssets.Character));
        }
    }

    // ------------------------------------------------------------ validation

    [Fact]
    public void The_known_slots_are_exactly_the_ones_the_code_reads()
    {
        Assert.Equal(new[] { "character", "companion", "sheets", "logo", "emptyState", "loadingState" }, SkinAssets.Known);
        Assert.Contains("sheets", SkinAssets.Known);
    }

    [Fact]
    public void A_clean_skin_reports_nothing()
    {
        var skin = new SkinManifest
        {
            Id = "demo",
            Name = "Demo",
            Assets = new Dictionary<string, string>
            {
                ["sheets"] = "demo_*",
                ["character"] = "demo_character",
                ["companion"] = "demo_xiaohei",
            },
        };

        var validation = SkinAssets.Validate(skin);

        Assert.True(validation.IsClean);
        Assert.Empty(validation.Unknown);
        Assert.Empty(validation.Empty);
    }

    [Theory]
    [InlineData("companian")]
    [InlineData("Companion")]
    [InlineData("character_sheet")]
    [InlineData("cat")]
    public void A_misspelled_slot_is_reported(string slot)
    {
        // Nothing reads an unknown slot, so nothing complains — the skin just shows
        // nothing where the author expected art. That silence is the bug.
        var skin = new SkinManifest
        {
            Id = "demo",
            Name = "Demo",
            Assets = new Dictionary<string, string> { [slot] = "some_asset" },
        };

        var validation = SkinAssets.Validate(skin);

        Assert.False(validation.IsClean);

        var finding = Assert.Single(validation.Unknown);
        Assert.Equal(slot, finding.Slot);
        Assert.Contains(slot, validation.Describe());
    }

    [Fact]
    public void Slot_names_are_case_sensitive_on_purpose()
    {
        // "Companion" and "companion" are not the same key, and pretending otherwise
        // would hide the typo instead of reporting it.
        var skin = new SkinManifest
        {
            Id = "demo",
            Name = "Demo",
            Assets = new Dictionary<string, string> { ["Companion"] = "x" },
        };

        Assert.Single(SkinAssets.Validate(skin).Unknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_declared_but_blank_slot_is_reported(string value)
    {
        var skin = new SkinManifest
        {
            Id = "demo",
            Name = "Demo",
            Assets = new Dictionary<string, string> { ["companion"] = value },
        };

        var validation = SkinAssets.Validate(skin);

        Assert.Single(validation.Empty);
        Assert.Contains("没有给出资源 id", validation.Describe());
    }

    [Fact]
    public void A_skin_with_no_assets_section_is_clean()
    {
        Assert.True(SkinAssets.Validate(new SkinManifest { Id = "demo", Name = "Demo" }).IsClean);
        Assert.True(SkinAssets.Validate(null).IsClean);
    }

    [Fact]
    public void Every_shipped_skin_declares_only_known_slots()
    {
        // This is the test that would have caught a typo in any of the six skins.
        foreach (var skin in Shipped())
        {
            var validation = SkinAssets.Validate(skin);

            Assert.True(validation.IsClean, $"{skin.Id}：{validation.Describe()}");
        }
    }

    [Fact]
    public void The_engine_actually_asks_for_this_validation()
    {
        // The validator is worthless if nothing calls it; this pins the call site.
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "EriReborn.Skin", "SkinEngine.cs"));

        Assert.Contains("SkinAssets.Validate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Eri_windows_declares_the_library_kuro_art_as_its_companion()
    {
        var skins = Shipped().ToList();
        var eriWindows = skins.Single(skin => skin.Id == "eri_windows");

        // The companion is real library art (04_Kuro_Cat / 26_Sprites), not a stand-in:
        // the walking strip, so the cat moves instead of sitting as one frozen frame.
        Assert.Equal("kuro_walk_sheet", SkinAssets.IdFor(eriWindows, SkinAssets.Companion));

        // Every shipped skin now fills the logo slot from its own element set.
        foreach (var skin in skins)
        {
            Assert.NotNull(SkinAssets.IdFor(skin, SkinAssets.Logo));
            Assert.NotNull(SkinAssets.IdFor(skin, SkinAssets.EmptyState));
            Assert.NotNull(SkinAssets.IdFor(skin, SkinAssets.LoadingState));
        }
    }
}
