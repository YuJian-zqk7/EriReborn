using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Views ask for a role, never for a shape. That indirection only pays off if
/// the role names are exact and a typo is loud: a misspelled role looks exactly
/// like a skin that chose not to override anything (spec 112).
/// </summary>
public sealed class CursorRoleTests
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

    [Fact]
    public void Every_role_round_trips_through_its_name()
    {
        Assert.Equal(10, CursorRoles.All.Count);

        foreach (var role in CursorRoles.All)
        {
            var name = CursorRoles.NameOf(role);
            Assert.True(CursorRoles.TryParse(name, out var parsed), $"'{name}' 无法解析回角色。");
            Assert.Equal(role, parsed);
        }
    }

    [Theory]
    [InlineData("Busy")]
    [InlineData("BUSY")]
    [InlineData("  busy  ")]
    public void Role_names_are_case_and_space_insensitive(string name)
    {
        Assert.True(CursorRoles.TryParse(name, out var role));
        Assert.Equal(CursorRole.Busy, role);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("resize_diagonal")]
    [InlineData("pointer")]
    public void An_invented_role_name_is_refused(string? name)
    {
        Assert.False(CursorRoles.TryParse(name, out _));
    }

    [Fact]
    public void A_keyword_declaration_is_normalised()
    {
        var spec = CursorSpec.Parse(CursorRole.Busy, "  Wait ");

        Assert.NotNull(spec);
        Assert.Equal("wait", spec!.Keyword);
        Assert.Null(spec.AssetId);
        Assert.False(spec.IsCustomArt);
    }

    [Fact]
    public void An_asset_declaration_points_at_art()
    {
        var spec = CursorSpec.Parse(CursorRole.Busy, "asset: tech_windows_controls");

        Assert.NotNull(spec);
        Assert.Equal("tech_windows_controls", spec!.AssetId);
        Assert.True(spec.IsCustomArt);
        Assert.Null(spec.Keyword);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("asset:")]
    [InlineData("asset:   ")]
    public void An_empty_declaration_is_not_a_declaration(string? value)
    {
        // Returning a spec for an empty value would override the default with nothing.
        Assert.Null(CursorSpec.Parse(CursorRole.Busy, value));
    }

    [Fact]
    public void A_skin_can_override_some_roles_and_leave_the_rest_alone()
    {
        var skin = new SkinManifest
        {
            Id = "custom",
            Name = "Custom",
            Cursors = new Dictionary<string, string>
            {
                ["busy"] = "appstarting",
                ["forbidden"] = "asset:custom_no",
            },
        };

        var (declared, unknown) = SkinCursors.Resolve(skin);

        Assert.Empty(unknown);
        Assert.Equal(2, declared.Count);
        Assert.Equal("appstarting", declared[CursorRole.Busy].Keyword);
        Assert.Equal("custom_no", declared[CursorRole.Forbidden].AssetId);

        // Everything else is simply absent, and the caller keeps the platform default.
        Assert.False(declared.ContainsKey(CursorRole.Drag));
    }

    [Fact]
    public void A_typo_is_reported_rather_than_silently_ignored()
    {
        var skin = new SkinManifest
        {
            Id = "typo",
            Name = "Typo",
            Cursors = new Dictionary<string, string>
            {
                ["busy"] = "wait",

                // Both are genuinely unknown: the parser is case-insensitive on
                // purpose, so "resizecorner" alone would be a valid spelling.
                ["resize_corner"] = "corner",
                ["resizediagonal"] = "sizeall",
            },
        };

        var (declared, unknown) = SkinCursors.Resolve(skin);

        Assert.Single(declared);
        Assert.Equal(2, unknown.Count);
        Assert.Contains("resize_corner", unknown);
        Assert.Contains("resizediagonal", unknown);
    }

    [Fact]
    public void A_skin_with_no_cursor_section_declares_nothing()
    {
        var (declared, unknown) = SkinCursors.Resolve(new SkinManifest { Id = "plain", Name = "Plain" });

        Assert.Empty(declared);
        Assert.Empty(unknown);
    }

    [Fact]
    public void The_cursor_section_is_read_from_the_skin_file()
    {
        var manifest = SkinManifestParser.Parse("""
        {
          "id": "cursored",
          "name": "Cursored",
          "cursors": { "busy": "appstarting", "drag": "sizeall" }
        }
        """);

        Assert.Equal(2, manifest.Cursors.Count);
        Assert.Equal("appstarting", manifest.Cursors["busy"]);
    }

    [Fact]
    public void No_view_hard_codes_a_pointer_shape_any_more()
    {
        // A literal Cursor="..." in a view is the thing this system replaces: it
        // bypasses the skin and cannot be themed.
        var views = Path.Combine(RepositoryRoot(), "src", "EriReborn.UI.Avalonia", "Views");

        var offenders = Directory
            .EnumerateFiles(views, "*.axaml")
            .Where(path => File.ReadAllText(path).Contains("Cursor=\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }
}
