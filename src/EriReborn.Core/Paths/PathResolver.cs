using EriReborn.Core.Catalog;
using EriReborn.Core.Domain;
using EriReborn.Core.Validation;

namespace EriReborn.Core.Paths;

/// <summary>
/// The single place that turns <see cref="EnvironmentContext"/> plus a
/// <see cref="SoftwareDefinition"/> into an on-disk directory (spec 18).
/// No script or view is allowed to hard-code install paths.
/// </summary>
public sealed class PathResolver
{
    public string ResolveSoftwareDirectory(EnvironmentContext context, SoftwareDefinition software)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(software);

        var overridePath = context.OverrideFor(software.Id);
        if (overridePath is not null)
        {
            return Normalize(overridePath);
        }

        var segments = new List<string> { context.RootPath, software.CategoryId };
        if (context.IncludeSubcategoryFolder && !string.IsNullOrWhiteSpace(software.SubcategoryId))
        {
            segments.Add(software.SubcategoryId);
        }

        segments.Add(software.DirectoryName);
        return Normalize(Path.Combine(segments.ToArray()));
    }

    /// <summary>Explains exactly why a resolved path is unacceptable.</summary>
    public ValidationResult ValidateSoftwareDirectory(EnvironmentContext context, SoftwareDefinition software)
    {
        var path = ResolveSoftwareDirectory(context, software);
        var issues = new List<ValidationIssue>(DirectoryNameValidator.ValidateFullPath(path).Issues);

        // The software's own official directory name is always validated,
        // even when the user overrode the parent path.
        var nameResult = DirectoryNameValidator.ValidateName(software.DirectoryName);
        if (!nameResult.IsValid)
        {
            issues.AddRange(nameResult.Issues.Select(i => i with { Subject = $"software:{software.Id}" }));
        }

        var categoryResult = CategoryTaxonomy.ValidateOfficialCategoryId(software.CategoryId);
        if (!categoryResult.IsValid)
        {
            issues.AddRange(categoryResult.Issues.Select(i => i with { Subject = $"software:{software.Id}" }));
        }

        return ValidationResult.From(issues);
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 3 && full.EndsWith(Path.DirectorySeparatorChar)
            ? full.TrimEnd(Path.DirectorySeparatorChar)
            : full;
    }
}
