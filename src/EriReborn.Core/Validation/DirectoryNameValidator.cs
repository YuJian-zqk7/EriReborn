using System.Text;

namespace EriReborn.Core.Validation;

/// <summary>
/// Enforces the official directory naming rule (spec 15/16): ASCII letters,
/// digits and underscore only. Invalid data is reported, never slugified.
/// </summary>
public static class DirectoryNameValidator
{
    public const int MaxSegmentLength = 64;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsAllowedChar(char c)
        => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_';

    /// <summary>Validates one official directory name / category id.</summary>
    public static ValidationResult ValidateName(string? name)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrEmpty(name))
        {
            issues.Add(new ValidationIssue("dir.empty", "Official name must not be empty."));
            return ValidationResult.From(issues);
        }

        if (name == "." || name == "..")
        {
            issues.Add(new ValidationIssue("dir.dot", $"'{name}' is a relative path token and is not allowed.", name));
        }

        if (name.Length > MaxSegmentLength)
        {
            issues.Add(new ValidationIssue("dir.too_long", $"Name exceeds {MaxSegmentLength} characters.", name));
        }

        if (name.EndsWith(' '))
        {
            issues.Add(new ValidationIssue("dir.trailing_space", "Name must not end with a space.", name));
        }

        if (name.EndsWith('.'))
        {
            issues.Add(new ValidationIssue("dir.trailing_period", "Name must not end with a period.", name));
        }

        var offenders = new StringBuilder();
        var hasNonAscii = false;
        foreach (var c in name)
        {
            if (IsAllowedChar(c))
            {
                continue;
            }

            if (c > 127)
            {
                hasNonAscii = true;
            }

            offenders.Append(c);
        }

        if (hasNonAscii)
        {
            issues.Add(new ValidationIssue("dir.not_ascii", "Name must contain ASCII characters only.", name));
        }

        if (offenders.Length > 0)
        {
            issues.Add(new ValidationIssue(
                "dir.illegal_char",
                $"Only A-Z, a-z, 0-9 and _ are allowed. Illegal characters: '{offenders}'.",
                name));
        }

        var stem = name.Split('.')[0];
        if (WindowsReservedNames.Contains(stem))
        {
            issues.Add(new ValidationIssue("dir.reserved", $"'{name}' is a reserved Windows device name.", name));
        }

        return ValidationResult.From(issues);
    }

    /// <summary>
    /// Detects duplicate and case-insensitive collisions inside one directory
    /// level (spec 16: sibling and case conflicts).
    /// </summary>
    public static ValidationResult ValidateSiblingSet(IEnumerable<string?> names)
    {
        var issues = new List<ValidationIssue>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenCaseInsensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (seen.TryGetValue(name, out var first))
            {
                issues.Add(new ValidationIssue("dir.conflict", $"Duplicate name also used by '{first}'.", name));
                continue;
            }

            if (seenCaseInsensitive.TryGetValue(name, out var firstCase))
            {
                issues.Add(new ValidationIssue(
                    "dir.case_conflict",
                    $"Name collides case-insensitively with '{firstCase}'.",
                    name));
            }

            seen[name] = name;
            seenCaseInsensitive[name] = name;
        }

        return ValidationResult.From(issues);
    }

    /// <summary>
    /// Checks that a fully resolved path is legal for Windows.
    /// The official ASCII rule is deliberately NOT applied here: it governs the
    /// directory names EriReborn itself defines, while the root directory the
    /// user picked only has to be a valid Windows path (spec 15/17).
    /// Callers validate the official segments separately.
    /// </summary>
    public static ValidationResult ValidateFullPath(string? path, int maxPathLength = 240)
    {
        var issues = new List<ValidationIssue>();

        if (string.IsNullOrWhiteSpace(path))
        {
            return ValidationResult.Fail("path.empty", "Resolved path must not be empty.");
        }

        if (path.Length > maxPathLength)
        {
            issues.Add(new ValidationIssue("path.too_long", $"Resolved path exceeds {maxPathLength} characters.", path));
        }

        if (!Path.IsPathFullyQualified(path))
        {
            issues.Add(new ValidationIssue("path.not_absolute", "Resolved path must be fully qualified.", path));
        }

        var invalidChars = Path.GetInvalidFileNameChars();

        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            // Skip empty segments and drive specifications such as "C:".
            if (segment.Length == 0 || segment.EndsWith(':'))
            {
                continue;
            }

            var illegal = segment.Where(c => Array.IndexOf(invalidChars, c) >= 0).Distinct().ToArray();
            if (illegal.Length > 0)
            {
                issues.Add(new ValidationIssue(
                    "path.illegal_char",
                    $"Path segment '{segment}' contains characters Windows does not allow: '{new string(illegal)}'.",
                    path));
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                issues.Add(new ValidationIssue(
                    "path.trailing",
                    $"Path segment '{segment}' must not end with a space or a period.",
                    path));
            }

            var stem = segment.Split('.')[0];
            if (WindowsReservedNames.Contains(stem))
            {
                issues.Add(new ValidationIssue(
                    "path.reserved",
                    $"Path segment '{segment}' uses a reserved Windows device name.",
                    path));
            }
        }

        return ValidationResult.From(issues);
    }

    /// <summary>
    /// Validates one EriReborn-defined segment (category id or official
    /// directory name) with the strict ASCII rule (spec 15).
    /// </summary>
    public static ValidationResult ValidateOfficialSegment(string? segment) => ValidateName(segment);
}
