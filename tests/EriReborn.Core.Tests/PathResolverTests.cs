using EriReborn.Core.Domain;
using EriReborn.Core.Paths;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>Spec 18: every install path is produced by the one resolver.</summary>
public sealed class PathResolverTests
{
    private static SoftwareDefinition Software(string id = "game", string category = "Games", string? sub = null, string dir = "GoodGames")
        => new()
        {
            Id = id,
            Name = id,
            CategoryId = category,
            SubcategoryId = sub,
            DirectoryName = dir,
        };

    [Fact]
    public void Builds_root_category_subcategory_directory()
    {
        var resolver = new PathResolver();
        var context = new EnvironmentContext { RootPath = @"E:\EriReborn" };
        var path = resolver.ResolveSoftwareDirectory(context, Software(sub: "Games_Launcher"));
        Assert.Equal(Path.GetFullPath(@"E:\EriReborn\Games\Games_Launcher\GoodGames"), path);
    }

    [Fact]
    public void Omits_subcategory_when_disabled()
    {
        var resolver = new PathResolver();
        var context = new EnvironmentContext { RootPath = @"E:\EriReborn", IncludeSubcategoryFolder = false };
        var path = resolver.ResolveSoftwareDirectory(context, Software(sub: "Games_Launcher"));
        Assert.Equal(Path.GetFullPath(@"E:\EriReborn\Games\GoodGames"), path);
    }

    [Fact]
    public void User_override_wins_over_every_other_rule()
    {
        var resolver = new PathResolver();
        var context = new EnvironmentContext
        {
            RootPath = @"E:\EriReborn",
            SoftwarePathOverrides = new Dictionary<string, string> { ["game"] = @"D:\MyOwn\Place" },
        };

        var path = resolver.ResolveSoftwareDirectory(context, Software());
        Assert.Equal(Path.GetFullPath(@"D:\MyOwn\Place"), path);
    }

    [Fact]
    public void Rejects_invalid_official_names_with_explicit_reasons()
    {
        var resolver = new PathResolver();
        var context = new EnvironmentContext { RootPath = @"E:\EriReborn" };
        var result = resolver.ValidateSoftwareDirectory(context, Software(category: "游戏", dir: "My Game"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "dir.not_ascii");
    }
}
