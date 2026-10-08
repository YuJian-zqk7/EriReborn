using EriReborn.Core.Validation;

namespace EriReborn.Core.Catalog;

/// <summary>One category level. Official ids are ASCII; plugin ids are validated too.</summary>
public sealed record CategoryDefinition(string Id, string DisplayName, string? ParentId, bool IsOfficial);

/// <summary>
/// The official first/second level taxonomy (spec 14/15) plus the legacy
/// migration map used when importing catalog data (spec 71).
/// </summary>
public static class CategoryTaxonomy
{
    public static readonly IReadOnlyList<CategoryDefinition> Official = new CategoryDefinition[]
    {
        new("System", "系统基础", null, true),
        new("System_Drivers", "驱动", "System", true),
        new("System_Security", "安全", "System", true),

        new("Runtime", "运行环境", null, true),
        new("Runtime_DotNet", ".NET", "Runtime", true),
        new("Runtime_VisualCpp", "Visual C++", "Runtime", true),
        new("Runtime_Java", "Java", "Runtime", true),

        new("Development", "开发工具", null, true),
        new("Development_Ide", "IDE", "Development", true),
        new("Development_Vcs", "版本控制", "Development", true),
        new("Development_Ai", "AI 开发", "Development", true),

        new("Network", "网络工具", null, true),
        new("Network_Download", "下载工具", "Network", true),
        new("Network_Browser", "浏览器", "Network", true),
        new("Network_Proxy", "网络代理", "Network", true),

        new("Games", "游戏", null, true),
        new("Games_Launcher", "游戏平台", "Games", true),
        new("Games_Utility", "游戏工具", "Games", true),

        new("Graphics", "图形设计", null, true),
        new("Graphics_2D", "平面绘画", "Graphics", true),
        new("Graphics_3D", "3D 建模", "Graphics", true),

        new("Media", "音视频", null, true),
        new("Office", "办公阅读", null, true),
        new("Browser", "浏览器扩展", null, true),
        new("Portable", "便携美化", null, true),
        new("Utility", "实用工具", null, true),
    };

    /// <summary>Legacy (v2) category label to official ASCII id.</summary>
    private static readonly Dictionary<string, string> LegacyCategoryMap = new(StringComparer.Ordinal)
    {
        ["系统基础"] = "System",
        ["Dev"] = "Development",
        ["开发AI"] = "Development",
        ["网络下载"] = "Network",
        ["平面绘画"] = "Graphics",
        ["3D建模"] = "Graphics",
        ["音视频"] = "Media",
        ["游戏"] = "Games",
        ["办公阅读"] = "Office",
        ["美化便携"] = "Portable",
        ["Edge 扩展"] = "Browser",
        ["Runtime"] = "Runtime",
        ["Utilities"] = "Utility",
    };

    private static readonly Dictionary<string, string> LegacySubcategoryMap = new(StringComparer.Ordinal)
    {
        ["Archive"] = "Utility",
        ["Editor"] = "Development",
        ["Search"] = "Utility",
        ["Productivity"] = "Utility",
        ["Download"] = "Network_Download",
    };

    public static string? MapLegacyCategory(string? legacy)
    {
        if (string.IsNullOrWhiteSpace(legacy))
        {
            return null;
        }

        if (LegacyCategoryMap.TryGetValue(legacy, out var mapped))
        {
            return mapped;
        }

        // A legacy value that is already a valid official id is kept as-is.
        return DirectoryNameValidator.ValidateName(legacy).IsValid ? legacy : null;
    }

    public static string? MapLegacySubcategory(string? legacy, string officialCategoryId)
    {
        if (string.IsNullOrWhiteSpace(legacy))
        {
            return null;
        }

        if (LegacySubcategoryMap.TryGetValue(legacy, out var mapped))
        {
            return mapped;
        }

        var candidate = $"{officialCategoryId}_{legacy}";
        return DirectoryNameValidator.ValidateName(candidate).IsValid ? candidate : null;
    }

    public static CategoryDefinition? Find(string id)
        => Official.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    public static bool IsKnownOfficialId(string id) => Find(id) is not null;

    /// <summary>Validates an official category id, returning the concrete reasons if invalid.</summary>
    public static ValidationResult ValidateOfficialCategoryId(string? id)
    {
        var nameResult = DirectoryNameValidator.ValidateName(id);
        if (!nameResult.IsValid)
        {
            return nameResult;
        }

        return IsKnownOfficialId(id!)
            ? ValidationResult.Valid
            : ValidationResult.Fail("category.unknown", "Not a registered official category id.", id);
    }
}
