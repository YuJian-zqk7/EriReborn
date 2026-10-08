namespace EriReborn.Engine.Ai;

/// <summary>
/// Compares a configured model against what the endpoint actually offers. A
/// working connection is not the same as a working model: the service can
/// answer /models and still not serve the model the user typed (spec 58/69).
/// </summary>
public static class AiModelCheck
{
    /// <summary>A warning to show, or null when the model is fine or unknowable.</summary>
    public static string? Evaluate(string? configured, IReadOnlyList<string> available)
    {
        if (available.Count == 0)
        {
            // Nothing was advertised, so nothing can be concluded either way.
            return null;
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            return $"尚未填写模型；该端点提供：{Join(available)}";
        }

        if (available.Contains(configured.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"该端点未提供模型 '{configured.Trim()}'；可用：{Join(available)}";
    }

    private static string Join(IReadOnlyList<string> models)
        => string.Join(", ", models.Take(8)) + (models.Count > 8 ? $"，等 {models.Count} 个" : string.Empty);
}
