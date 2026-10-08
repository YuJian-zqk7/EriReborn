using EriReborn.Core.Catalog;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The official category tree and the legacy label migration.
///
/// <para>
/// Mutation testing found this file mentioned nowhere in the suite, which left two
/// silent failure modes open: a taxonomy that is internally inconsistent (a
/// duplicate id, a parent that does not exist, a third level nobody meant to add)
/// and a legacy map whose targets do not exist, which would file imported software
/// under a category that is not in the tree.
/// </para>
/// </summary>
public sealed class CategoryTaxonomyTests
{
    // --------------------------------------------------- structural integrity

    [Fact]
    public void Official_ids_are_unique()
    {
        var duplicates = CategoryTaxonomy.Official
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Official_ids_are_ascii_because_a_folder_name_is_made_from_them()
    {
        foreach (var category in CategoryTaxonomy.Official)
        {
            Assert.True(
                category.Id.All(c => c < 128),
                $"类别 id '{category.Id}' 含非 ASCII 字符，而目录名由它拼出。");
        }
    }

    [Fact]
    public void Every_parent_exists()
    {
        var ids = CategoryTaxonomy.Official.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var category in CategoryTaxonomy.Official.Where(c => c.ParentId is not null))
        {
            Assert.True(ids.Contains(category.ParentId!), $"'{category.Id}' 的父级 '{category.ParentId}' 不在分类表中。");
        }
    }

    [Fact]
    public void The_tree_is_two_levels_deep()
    {
        // Spec 14/15 stops at two. A third level would never be rendered, so the
        // software filed under it would simply be missing from the UI.
        var byId = CategoryTaxonomy.Official.ToDictionary(c => c.Id, StringComparer.Ordinal);

        foreach (var category in CategoryTaxonomy.Official.Where(c => c.ParentId is not null))
        {
            var parent = byId[category.ParentId!];
            Assert.Null(parent.ParentId);
        }
    }

    [Fact]
    public void Every_category_is_either_a_root_or_a_child_of_one_root()
    {
        var byId = CategoryTaxonomy.Official.ToDictionary(c => c.Id, StringComparer.Ordinal);

        foreach (var category in CategoryTaxonomy.Official)
        {
            if (category.ParentId is null)
            {
                Assert.Null(byId.GetValueOrDefault(category.Id)?.ParentId);
            }
            else
            {
                Assert.True(byId.ContainsKey(category.ParentId));
                Assert.NotEqual(category.Id, category.ParentId);
            }
        }
    }

    [Fact]
    public void Official_categories_say_they_are_official()
    {
        Assert.All(CategoryTaxonomy.Official, c => Assert.True(c.IsOfficial, $"{c.Id} 标为非官方。"));
    }

    [Fact]
    public void Every_category_has_a_display_name()
    {
        Assert.All(CategoryTaxonomy.Official, c => Assert.False(string.IsNullOrWhiteSpace(c.DisplayName)));
    }

    [Fact]
    public void There_is_at_least_one_root_and_the_ids_are_ordered_roots_first()
    {
        Assert.Contains(CategoryTaxonomy.Official, c => c.ParentId is null);
        Assert.True(CategoryTaxonomy.Official.Count > 5);
    }

    // ------------------------------------------------------------- lookup

    [Fact]
    public void Lookup_is_exact_and_ordinal()
    {
        // A case-insensitive lookup would accept "system" and then build a folder
        // named after what the caller typed, not the category that exists.
        Assert.NotNull(CategoryTaxonomy.Find("System"));
        Assert.Null(CategoryTaxonomy.Find("system"));
        Assert.Null(CategoryTaxonomy.Find("SYSTEM"));
        Assert.Null(CategoryTaxonomy.Find("does-not-exist"));

        // Deliberately outside the contract: the parameter is non-nullable, and a
        // caller who violates that must still not get an exception out of a lookup.
        Assert.Null(CategoryTaxonomy.Find(null!));
    }

    [Fact]
    public void IsKnown_agrees_with_Find_for_every_category()
    {
        foreach (var category in CategoryTaxonomy.Official)
        {
            Assert.True(CategoryTaxonomy.IsKnownOfficialId(category.Id));
            Assert.NotNull(CategoryTaxonomy.Find(category.Id));
        }

        Assert.False(CategoryTaxonomy.IsKnownOfficialId("not-a-category"));
    }

    [Fact]
    public void A_known_id_validates_and_an_unknown_one_is_refused_by_name()
    {
        Assert.True(CategoryTaxonomy.ValidateOfficialCategoryId("Runtime_DotNet").IsValid);

        var unknown = CategoryTaxonomy.ValidateOfficialCategoryId("Nonsense");
        Assert.False(unknown.IsValid);
        Assert.Contains(unknown.Issues, issue => issue.Code == "category.unknown");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_id_is_refused(string? id)
    {
        Assert.False(CategoryTaxonomy.ValidateOfficialCategoryId(id).IsValid);
    }

    // -------------------------------------------------- legacy migration

    [Fact]
    public void Every_legacy_category_target_is_a_real_category()
    {
        // A mapping to an id that is not in the tree files imported software
        // somewhere nothing can display.
        string[] legacyLabels =
        {
            "系统基础", "Dev", "开发AI", "网络下载", "平面绘画", "3D建模",
            "音视频", "游戏", "办公阅读", "美化便携", "Edge 扩展", "Runtime", "Utilities",
        };

        foreach (var label in legacyLabels)
        {
            var mapped = CategoryTaxonomy.MapLegacyCategory(label);
            Assert.NotNull(mapped);
            Assert.True(
                CategoryTaxonomy.IsKnownOfficialId(mapped!),
                $"旧版类别 '{label}' 映射到 '{mapped}'，但它不是官方类别。");
        }
    }

    [Fact]
    public void Every_legacy_subcategory_target_is_a_real_category()
    {
        string[] legacyLabels = { "Archive", "Editor", "Search", "Productivity", "Download" };

        foreach (var label in legacyLabels)
        {
            var mapped = CategoryTaxonomy.MapLegacySubcategory(label, "Utility");
            Assert.NotNull(mapped);
            Assert.True(
                CategoryTaxonomy.IsKnownOfficialId(mapped!),
                $"旧版子类别 '{label}' 映射到 '{mapped}'，但它不是官方类别。");
        }
    }

    [Fact]
    public void A_legacy_label_that_is_already_an_official_id_is_kept()
    {
        Assert.Equal("Runtime", CategoryTaxonomy.MapLegacyCategory("Runtime"));
        Assert.Equal("Utility", CategoryTaxonomy.MapLegacyCategory("Utility"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_legacy_label_maps_to_nothing(string? label)
    {
        Assert.Null(CategoryTaxonomy.MapLegacyCategory(label));
    }

    [Theory]
    [InlineData("这不是合法名字")]
    [InlineData("with/slash")]
    [InlineData("with:colon")]
    public void A_legacy_label_that_cannot_be_a_folder_is_refused(string label)
    {
        // Rather than being kept as a category id that could never become a folder.
        Assert.Null(CategoryTaxonomy.MapLegacyCategory(label));
    }

    [Fact]
    public void An_unmapped_legacy_subcategory_is_composed_from_its_parent()
    {
        Assert.Equal("Utility_Archive2", CategoryTaxonomy.MapLegacySubcategory("Archive2", "Utility"));
    }

    [Fact]
    public void A_mapped_legacy_subcategory_uses_the_table_not_the_parent()
    {
        Assert.Equal("Utility", CategoryTaxonomy.MapLegacySubcategory("Archive", "Whatever"));
    }

    [Fact]
    public void Mapping_is_deterministic()
    {
        Assert.Equal(
            CategoryTaxonomy.MapLegacyCategory("游戏"),
            CategoryTaxonomy.MapLegacyCategory("游戏"));
    }
}
