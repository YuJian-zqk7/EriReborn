using System.Text.RegularExpressions;
using EriReborn.Core.Logging;
using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A skin is not a palette (spec 40/102): its metrics, control sizes, navigation
/// and window rules have to reach the UI. A value that is declared, resolved and
/// projected but read by nothing is indistinguishable from a feature that does
/// not exist, so that is what these tests check.
/// </summary>
public sealed class SkinWiringTests
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

    private static DirectoryInfo SkinDirectory()
        => new DirectoryInfo(Path.Combine(RepositoryRoot(), "assets", "skins"));

    private static SkinManifest LoadSkin(string id)
    {
        var file = SkinDirectory()
            .EnumerateFiles("skin.json", SearchOption.AllDirectories)
            .First(path => path.Directory?.Name == id);

        return SkinManifestParser.Parse(File.ReadAllText(file.FullName), file.FullName);
    }

    // ------------------------------------------------------------- coverage

    /// <summary>
    /// Every word a skin may replace has to be a word something shows.
    ///
    /// <para>
    /// The editor offers one row per key, so a key no control reads is an edit that changes nothing
    /// on screen — worse than having no key at all, because it reads as the whole feature being
    /// broken. UiTexts.cs is excluded because it defines the keys and would otherwise vouch for
    /// every one of them.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_interface_word_is_read_by_something()
    {
        var root = RepositoryRoot();

        var sources = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => !path.EndsWith("UiTexts.cs", StringComparison.Ordinal))
            .ToList();

        var text = string.Join("\n", sources.Select(File.ReadAllText));

        // Quoted, because that is how a key is written in both places it is used: a C# string, or an
        // attribute in a view (<controls:UiText Key="common.cancel" />).
        var unread = UiTexts.Keys
            .Where(key => !text.Contains($"\"{key}\"", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(unread);
    }

    /// <summary>
    /// Every element a view declares is one the catalog can name.
    ///
    /// <para>
    /// A view marks what is editable with an id, and when the catalog has never heard of that id the panel
    /// shows the raw id itself — <c>software.plan_titel</c> instead of "软件：安装计划标题". One letter out of
    /// place is invisible here and glaring there, so the pair is checked as a pair. The two ids written as
    /// bindings are the navigation tree's: every row resolves its own, and there is no single id to know.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_element_a_view_declares_is_one_the_catalog_knows()
    {
        var root = RepositoryRoot();

        var declared = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.axaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"SkinElement\.Id=""([^""]+)""").Cast<Match>())
            .Select(match => match.Groups[1].Value)
            .Where(id => !id.StartsWith("{", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Sanity: a pattern that matches nothing would make this test vouch for nothing.
        Assert.NotEmpty(declared);

        Assert.Empty(declared.Where(id => !SkinElementCatalog.IsKnown(id)).ToList());
    }

    [Fact]
    public void Every_projected_skin_resource_is_read_by_something()
    {
        var root = RepositoryRoot();
        var applier = Path.Combine(root, "src", "EriReborn.UI.Avalonia", "SkinResourceApplier.cs");

        // A verbatim string keeps the pattern readable: doubled quotes only.
        var projected = Regex
            .Matches(File.ReadAllText(applier), @"resources\[""(Skin[^""]+)""\]")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(projected);

        var sources = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => !path.EndsWith("SkinResourceApplier.cs", StringComparison.Ordinal))
            .ToList();

        var text = string.Join("\n", sources.Select(File.ReadAllText));

        var unread = projected.Where(key => !text.Contains(key, StringComparison.Ordinal)).ToList();

        Assert.Empty(unread);
    }

    [Fact]
    public void The_applier_does_not_project_inputs_that_are_never_read()
    {
        var root = RepositoryRoot();
        var applier = File.ReadAllText(Path.Combine(root, "src", "EriReborn.UI.Avalonia", "SkinResourceApplier.cs"));

        // These are inputs to the derivation. Projecting them as well would create
        // resources nothing binds to.
        Assert.DoesNotContain(@"""SkinDensity""", applier);
        Assert.DoesNotContain(@"""SkinTouchTarget""", applier);
    }

    // --------------------------------------------------- the values arrive

    [Fact]
    public void A_mobile_skin_and_a_desktop_skin_resolve_to_different_metrics()
    {
        var mobile = SkinValueSet.FromManifest(LoadSkin("eri_android"));
        var desktop = SkinValueSet.FromManifest(LoadSkin("eri_windows"));

        Assert.True(mobile.ButtonHeight > desktop.ButtonHeight);
        Assert.True(mobile.NavItemHeight > desktop.NavItemHeight);
        Assert.True(mobile.InputHeight > desktop.InputHeight);
        Assert.True(mobile.MinTargetHeight >= 48);
        Assert.Equal(desktop.ButtonHeight, desktop.MinTargetHeight);
    }

    [Fact]
    public void A_compact_skin_really_is_more_compact()
    {
        var compact = SkinValueSet.FromManifest(LoadSkin("tech_windows"));
        var comfortable = SkinValueSet.FromManifest(LoadSkin("eri_windows"));

        // Density has to change something visible, or the setting is decoration.
        Assert.Equal("compact", compact.Density);
        Assert.True(compact.CardPadding < comfortable.CardPadding);
        Assert.True(compact.Spacing < comfortable.Spacing);
    }

    [Fact]
    public void A_borderless_skin_says_so()
    {
        Assert.Equal("borderless", SkinValueSet.FromManifest(LoadSkin("eri_android")).WindowChrome);
        Assert.Equal("system", SkinValueSet.FromManifest(LoadSkin("eri_windows")).WindowChrome);
    }

    [Fact]
    public void A_mobile_skin_asks_for_bottom_navigation()
    {
        Assert.Equal("bottom", SkinValueSet.FromManifest(LoadSkin("eri_android")).NavPosition);
        Assert.Equal("left", SkinValueSet.FromManifest(LoadSkin("eri_windows")).NavPosition);
    }

    [Fact]
    public void Every_shipped_skin_resolves_without_a_fallback_gap()
    {
        foreach (var file in SkinDirectory().EnumerateFiles("skin.json", SearchOption.AllDirectories))
        {
            var skin = SkinManifestParser.Parse(File.ReadAllText(file.FullName), file.FullName);
            var values = SkinValueSet.FromManifest(skin);

            Assert.False(string.IsNullOrWhiteSpace(values.SkinId));
            Assert.True(values.ButtonHeight > 0);
            Assert.True(values.NavItemHeight > 0);
            Assert.True(values.CardPadding > 0);
            Assert.Contains(values.NavPosition, new[] { "left", "right", "bottom", "top" });
            Assert.Contains(values.WindowChrome, new[] { "system", "borderless" });
        }
    }
}
