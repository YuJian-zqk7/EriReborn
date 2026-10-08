using EriReborn.Engine.Ai;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A reachable endpoint can still refuse the model the user typed. That mismatch
/// must be reported, not discovered later at request time (spec 58/69).
/// </summary>
public sealed class AiModelCheckTests
{
    [Fact]
    public void An_offered_model_produces_no_warning()
    {
        Assert.Null(AiModelCheck.Evaluate("deepseek-flash", new[] { "deepseek-flash", "deepseek-v4-pro" }));
    }

    [Fact]
    public void Matching_is_case_insensitive_and_ignores_padding()
    {
        Assert.Null(AiModelCheck.Evaluate("  DeepSeek-Flash ", new[] { "deepseek-flash" }));
    }

    [Fact]
    public void A_model_the_endpoint_does_not_offer_is_reported_with_the_alternatives()
    {
        var warning = AiModelCheck.Evaluate("deepseek-chat", new[] { "deepseek-flash", "deepseek-v4-pro" });

        Assert.NotNull(warning);
        Assert.Contains("deepseek-chat", warning);
        Assert.Contains("deepseek-flash", warning);
        Assert.Contains("deepseek-v4-pro", warning);
    }

    [Fact]
    public void An_endpoint_that_advertises_nothing_is_not_accused_of_anything()
    {
        // Some compatible endpoints do not implement /models; silence is correct.
        Assert.Null(AiModelCheck.Evaluate("anything", Array.Empty<string>()));
    }

    [Fact]
    public void An_empty_model_is_pointed_out_along_with_what_is_available()
    {
        var warning = AiModelCheck.Evaluate(null, new[] { "deepseek-flash" });

        Assert.NotNull(warning);
        Assert.Contains("deepseek-flash", warning);
    }

    [Fact]
    public void A_long_model_list_is_truncated_but_counted()
    {
        var many = Enumerable.Range(1, 20).Select(i => $"model-{i}").ToArray();

        var warning = AiModelCheck.Evaluate("nope", many);

        Assert.NotNull(warning);
        Assert.Contains("等 20 个", warning);
    }
}
