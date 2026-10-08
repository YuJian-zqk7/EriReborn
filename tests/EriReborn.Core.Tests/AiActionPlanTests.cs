using EriReborn.Engine.Ai;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The model proposes; the application decides what is expressible and what runs.
/// These tests are the fence: anything not on the closed list must be refused
/// before the user is ever asked to approve it (spec 57/59).
/// </summary>
public sealed class AiActionPlanTests
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "vlc", "git", "python313",
    };

    /// <summary>A backtick built at run time, so this file contains no fence of its own.</summary>
    private static string Tick => new((char)96, 3);

    [Fact]
    public void The_vocabulary_is_closed_and_tiny()
    {
        // Every action here is something the application can explain and undo. A
        // longer list would mean a larger surface for a model to act through.
        var kinds = Enum.GetValues<AiActionKind>();

        Assert.Equal(2, kinds.Length);
        Assert.Contains(AiActionKind.Install, kinds);
        Assert.Contains(AiActionKind.Hint, kinds);
    }

    [Fact]
    public void A_well_formed_plan_is_accepted()
    {
        var plan = AiActionPlanner.Parse("""
        {
          "summary": "缺少播放器，且有个工具检测不到",
          "actions": [
            { "kind": "install", "target": "vlc", "reason": "没有播放器" },
            { "kind": "hint", "target": "git", "value": "^Git", "reason": "检测不到" }
          ]
        }
        """, Known);

        Assert.Equal(2, plan.Accepted.Count);
        Assert.Empty(plan.Rejected);
        Assert.Contains("缺少播放器", plan.Summary);
        Assert.Equal(AiActionKind.Hint, plan.Accepted[1].Kind);
        Assert.Equal("^Git", plan.Accepted[1].Value);
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("registry")]
    [InlineData("download")]
    [InlineData("delete")]
    public void An_invented_action_kind_is_refused(string kind)
    {
        var plan = AiActionPlanner.Parse(
            "{ \"actions\": [ { \"kind\": \"" + kind + "\", \"target\": \"vlc\", \"value\": \"whatever\" } ] }",
            Known);

        // A model that invents a kind is testing whether anything off the list gets
        // executed. Saying no is the whole feature.
        Assert.Empty(plan.Accepted);
        Assert.Contains("不支持", Assert.Single(plan.Rejected).Reason);
    }

    [Fact]
    public void An_id_the_catalog_does_not_have_is_refused()
    {
        var plan = AiActionPlanner.Parse(
            "{ \"actions\": [ { \"kind\": \"install\", \"target\": \"not-in-the-catalog\" } ] }",
            Known);

        Assert.Empty(plan.Accepted);
        Assert.Contains("清单里没有", Assert.Single(plan.Rejected).Reason);
    }

    [Fact]
    public void The_same_target_twice_is_refused_the_second_time()
    {
        var plan = AiActionPlanner.Parse(
            "{ \"actions\": ["
            + "{ \"kind\": \"install\", \"target\": \"vlc\" },"
            + "{ \"kind\": \"install\", \"target\": \"vlc\" }] }",
            Known);

        Assert.Single(plan.Accepted);
        Assert.Contains("重复", Assert.Single(plan.Rejected).Reason);
    }

    [Fact]
    public void A_hint_without_a_pattern_is_refused()
    {
        // A hint that matches nothing is worse than no hint: the entry would look
        // configured and still never be detected.
        var plan = AiActionPlanner.Parse(
            "{ \"actions\": [ { \"kind\": \"hint\", \"target\": \"git\" } ] }",
            Known);

        Assert.Empty(plan.Accepted);
        Assert.Contains("匹配模式", Assert.Single(plan.Rejected).Reason);
    }

    [Fact]
    public void A_plan_longer_than_the_limit_is_trimmed_and_says_so()
    {
        var actions = string.Join(",", new[] { "vlc", "git", "python313", "vlc", "git" }
            .Select(id => "{ \"kind\": \"install\", \"target\": \"" + id + "\" }"));

        var plan = AiActionPlanner.Parse("{ \"actions\": [" + actions + "] }", Known, maxActions: 2);

        Assert.Equal(2, plan.Accepted.Count);
        Assert.Equal(3, plan.Rejected.Count);
        Assert.Contains(plan.Rejected, rejection => rejection.Reason.Contains("最多"));
    }

    [Fact]
    public void An_unparseable_answer_is_a_rejection_not_a_crash()
    {
        var plan = AiActionPlanner.Parse("<html>gateway error</html>", Known);

        Assert.Empty(plan.Accepted);
        Assert.Single(plan.Rejected);
    }

    [Fact]
    public void Json_wrapped_in_prose_or_a_fence_is_still_read()
    {
        // Models like to explain themselves first, and a plan refused because of a
        // stray fence is a plan the user never sees.
        var answer = "这是我的建议：\n" + Tick + "json\n"
            + "{ \"actions\": [ { \"kind\": \"install\", \"target\": \"vlc\" } ] }\n"
            + Tick + "\n希望有帮助。";

        var plan = AiActionPlanner.Parse(answer, Known);

        Assert.Single(plan.Accepted);
    }

    [Fact]
    public void Braces_in_the_prose_around_the_plan_do_not_swallow_it()
    {
        // A model that explains itself with an example — "each action looks like { kind, target }" — used
        // to break the parse: the slice ran from the prose's brace to the last one in the answer, so the
        // text handed to the parser began in a sentence and could never be JSON. The plan was there, one
        // sentence below the words that hid it.
        var answer = "每条大致长这样（kind/target）：「{ kind, target }」。下面是结果：\n"
            + "{ \"summary\": \"装个播放器\", \"actions\": [ { \"kind\": \"install\", \"target\": \"vlc\" } ] }\n"
            + "其中 { } 表示空对象。";

        var plan = AiActionPlanner.Parse(answer, Known);

        Assert.Single(plan.Accepted);
        Assert.Empty(plan.Rejected);
        Assert.Equal("vlc", plan.Accepted[0].Target);
    }

    [Fact]
    public void A_brace_after_the_plan_does_not_truncate_or_extend_it()
    {
        // The other half of the same mistake: the last brace in the answer is often in the closing prose,
        // which used to stretch the slice past the object and produce a parse error for a plan that was
        // complete and correct.
        var answer = "{ \"actions\": [ { \"kind\": \"install\", \"target\": \"git\" } ] }\n"
            + "若要改成别的，回复 { \"actions\": [] } 即可。";

        var plan = AiActionPlanner.Parse(answer, Known);

        Assert.Single(plan.Accepted);
        Assert.Equal("git", plan.Accepted[0].Target);
    }

    [Fact]
    public void A_brace_inside_a_string_is_text_and_not_structure()
    {
        // A brace in any string — a reason, a summary, a file name — is a character, not the end of the
        // object. Counting it as structure closed the JSON early and made the rest look like garbage.
        var answer = "{ \"summary\": \"路径以 } 结尾\", \"actions\": ["
            + "{ \"kind\": \"install\", \"target\": \"vlc\", \"reason\": \"文件名含 { 和 }\" }] }";

        var plan = AiActionPlanner.Parse(answer, Known);

        Assert.Single(plan.Accepted);
        Assert.Contains("}", plan.Summary);
    }

    [Fact]
    public void An_empty_plan_is_empty_rather_than_an_error()
    {
        var plan = AiActionPlanner.Parse("{ \"summary\": \"一切正常\", \"actions\": [] }", Known);

        Assert.Empty(plan.Accepted);
        Assert.Empty(plan.Rejected);
        Assert.False(plan.HasActions);
    }

    [Fact]
    public void Nothing_is_silently_dropped()
    {
        var plan = AiActionPlanner.Parse(
            "{ \"actions\": ["
            + "{ \"kind\": \"install\", \"target\": \"vlc\" },"
            + "{ \"kind\": \"install\", \"target\": \"ghost\" },"
            + "{ \"kind\": \"teleport\", \"target\": \"git\" }] }",
            Known);

        // One accepted, two refused and both reported: a plan that quietly shrank
        // between the model and the screen is one the user approves blind.
        Assert.Single(plan.Accepted);
        Assert.Equal(2, plan.Rejected.Count);
    }

    [Fact]
    public void The_contract_told_to_the_model_forbids_commands_and_paths()
    {
        Assert.Contains("不要输出命令", AiActionPlanner.ContractHint);
        Assert.Contains("清单以外", AiActionPlanner.ContractHint);
        Assert.Contains("install", AiActionPlanner.ContractHint);
        Assert.Contains("hint", AiActionPlanner.ContractHint);
    }

    [Fact]
    public async Task The_proposal_prompt_says_the_model_cannot_change_anything()
    {
        // Asserted on the request that really goes out, not on the constant: what the
        // model is told is the thing that matters.
        string? body = null;

        using var client = new HttpClient(new Handler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"choices\": [ { \"message\": { \"content\": \"{}\" } } ] }"),
            };
        }));

        var digest = AiEnvironmentDigestBuilder.Build(new AiEnvironmentFacts { OperatingSystem = "Windows 11" });

        await AiEnvironmentAnalyst.ProposeAsync(client, "https://ai.invalid/v1", "k", "m", digest);

        Assert.NotNull(body);

        using var sent = System.Text.Json.JsonDocument.Parse(body!);
        var system = sent.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;

        // A model asked to "fix it" answers as if it had, and the user then reads a
        // change log for changes that never happened.
        Assert.Contains("没有修改它的能力", system);
        Assert.Contains("待用户确认", system);

        // And it is told the vocabulary is closed.
        Assert.Contains("install", system);
        Assert.Contains("hint", system);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
