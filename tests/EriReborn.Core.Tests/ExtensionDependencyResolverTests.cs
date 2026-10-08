using EriReborn.Extension;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Declared dependencies must decide what loads, in what order, and what cannot
/// load at all (spec 34/35). Previously the list was parsed and displayed but
/// never consulted.
/// </summary>
public sealed class ExtensionDependencyResolverTests
{
    private static ExtensionManifest Manifest(string id, params string[] dependencies) => new()
    {
        Id = id,
        Name = id,
        Assembly = $"{id}.dll",
        Dependencies = dependencies,
    };

    [Fact]
    public void Extensions_without_dependencies_all_load()
    {
        var result = ExtensionDependencyResolver.Resolve(new[]
        {
            Manifest("alpha"),
            Manifest("beta"),
        });

        Assert.Empty(result.Blocked);
        Assert.Equal(new[] { "alpha", "beta" }, result.LoadOrder);
    }

    [Fact]
    public void A_dependency_loads_before_the_extension_that_needs_it()
    {
        var result = ExtensionDependencyResolver.Resolve(new[]
        {
            Manifest("app", "lib"),
            Manifest("lib"),
        });

        Assert.Empty(result.Blocked);
        Assert.Equal(new[] { "lib", "app" }, result.LoadOrder);
    }

    [Fact]
    public void Transitive_dependencies_are_ordered_deepest_first()
    {
        var result = ExtensionDependencyResolver.Resolve(new[]
        {
            Manifest("app", "mid"),
            Manifest("mid", "base"),
            Manifest("base"),
        });

        Assert.Empty(result.Blocked);
        Assert.Equal(new[] { "base", "mid", "app" }, result.LoadOrder);
    }

    [Fact]
    public void A_diamond_orders_the_shared_dependency_first_and_the_root_last()
    {
        var result = ExtensionDependencyResolver.Resolve(new[]
        {
            Manifest("app", "left", "right"),
            Manifest("left", "shared"),
            Manifest("right", "shared"),
            Manifest("shared"),
        });

        Assert.Empty(result.Blocked);
        Assert.Equal("shared", result.LoadOrder[0]);
        Assert.Equal("app", result.LoadOrder[^1]);
        Assert.Equal(4, result.LoadOrder.Count);
    }

    [Fact]
    public void A_missing_dependency_blocks_the_extension_and_names_it()
    {
        var result = ExtensionDependencyResolver.Resolve(new[] { Manifest("app", "ghost") });

        Assert.DoesNotContain("app", result.LoadOrder);
        Assert.True(result.IsBlocked("app"));
        Assert.Contains("ghost", result.Blocked["app"]);
    }

    [Fact]
    public void A_disabled_dependency_blocks_its_dependents_and_says_so()
    {
        var result = ExtensionDependencyResolver.Resolve(
            new[] { Manifest("app", "lib"), Manifest("lib") },
            unavailable: new[] { "lib" });

        Assert.True(result.IsBlocked("app"));
        Assert.Contains("禁用", result.Blocked["app"]);
        Assert.DoesNotContain("lib", result.LoadOrder);
    }

    [Fact]
    public void A_cycle_blocks_every_member_instead_of_hanging()
    {
        var result = ExtensionDependencyResolver.Resolve(new[]
        {
            Manifest("a", "b"),
            Manifest("b", "a"),
        });

        Assert.True(result.IsBlocked("a"));
        Assert.True(result.IsBlocked("b"));
        Assert.Contains("循环", result.Blocked["a"]);
        Assert.Empty(result.LoadOrder);
    }

    [Fact]
    public void A_self_dependency_is_rejected_rather_than_treated_as_a_cycle_of_one()
    {
        var result = ExtensionDependencyResolver.Resolve(new[] { Manifest("selfish", "selfish") });

        Assert.True(result.IsBlocked("selfish"));
        Assert.Empty(result.LoadOrder);
    }

    [Fact]
    public void An_extension_needing_a_blocked_extension_is_itself_blocked()
    {
        var result = ExtensionDependencyResolver.Resolve(new[]
        {
            Manifest("app", "mid"),
            Manifest("mid", "ghost"),
        });

        Assert.True(result.IsBlocked("mid"));
        Assert.True(result.IsBlocked("app"));
        Assert.Contains("mid", result.Blocked["app"]);
        Assert.Empty(result.LoadOrder);
    }

    [Fact]
    public void A_dependency_that_is_merely_absent_is_reported_as_not_installed()
    {
        var result = ExtensionDependencyResolver.Resolve(new[] { Manifest("app", "ghost") });

        Assert.True(result.IsBlocked("ghost"));
        Assert.Contains("并未安装", result.Blocked["ghost"]);
    }

    [Fact]
    public void An_empty_catalog_resolves_to_nothing()
    {
        var result = ExtensionDependencyResolver.Resolve(Array.Empty<ExtensionManifest>());

        Assert.Empty(result.LoadOrder);
        Assert.Empty(result.Blocked);
    }

    [Fact]
    public void Duplicate_dependency_declarations_do_not_duplicate_the_order()
    {
        var result = ExtensionDependencyResolver.Resolve(new[]
        {
            Manifest("app", "lib", "lib", "lib"),
            Manifest("lib"),
        });

        Assert.Equal(new[] { "lib", "app" }, result.LoadOrder);
    }
}
