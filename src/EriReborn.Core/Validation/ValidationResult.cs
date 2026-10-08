namespace EriReborn.Core.Validation;

/// <summary>
/// A single, addressable validation problem. Errors are never silently
/// slugified away (spec 16).
/// </summary>
public sealed record ValidationIssue(string Code, string Message, string? Subject = null)
{
    public override string ToString() => Subject is null ? $"{Code}: {Message}" : $"{Code}: {Message} ({Subject})";
}

public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public static readonly ValidationResult Valid = new(Array.Empty<ValidationIssue>());

    public bool IsValid => Issues.Count == 0;

    public static ValidationResult Fail(string code, string message, string? subject = null)
        => new(new[] { new ValidationIssue(code, message, subject) });

    public static ValidationResult From(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.ToList();
        return list.Count == 0 ? Valid : new ValidationResult(list);
    }

    public ValidationResult Merge(ValidationResult other)
        => From(Issues.Concat(other.Issues));

    public string Describe() => string.Join("; ", Issues.Select(i => i.ToString()));
}
