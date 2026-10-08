using EriReborn.Cloud;
using EriReborn.Engine.Ai;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// What a model is allowed to contribute to a plugin, and what it is not.
///
/// <para>
/// The whole point of the generator is that the model never has to be trusted. It is shown the real
/// contents of a share, and everything it says is checked against that listing: an id it invented, a
/// hash it made up, a download address it guessed and a version nobody verified are all refused or
/// dropped, and each refusal is reported rather than swallowed.
/// </para>
///
/// <para>
/// The second half pins the other end: a capability is asked for through whichever service the user
/// configured, so switching between DeepSeek, Qwen and a local Ollama changes nothing in the code.
/// </para>
/// </summary>
public sealed class PluginDraftTests
{
    private const string ShareUrl = "https://share.example.test/s/abc";

    private static PluginCandidateTree Tree() => new(
        "123",
        ShareUrl,
        new[]
        {
            new PluginCandidate("1001", "Tool.zip", false, 1024L, "工具/Tool.zip"),
            new PluginCandidate("1002", "Games", true, null, "Games"),
        });

    /// <summary>A provider whose answer this test decides, so the protocol question stays out of the way.</summary>
    private sealed class RecordingProvider(string id, string reply) : IAiProvider
    {
        public int Calls { get; private set; }

        public string? LastSystemPrompt { get; private set; }

        public string? LastUserPrompt { get; private set; }

        public string Id { get; } = id;

        public string DisplayName => Id;

        public string DefaultBaseUrl => "https://stub.invalid/v1";

        public string DefaultModel => "stub-model";

        public bool RequiresKey => false;

        public AiProviderCapabilities Capabilities { get; } = new(
            AiImplementationKind.OfficialApi,
            ModelListing: false,
            ChatCompletion: true,
            AiAuthStyle.None,
            "测试替身。");

        public Task<AiProbeResult> ProbeAsync(
            HttpClient client,
            string? baseUrl,
            string? apiKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AiProbeResult.Fail("测试替身不提供模型列表。"));

        public Task<AiAnalysisResult> CompleteAsync(
            HttpClient client,
            string? baseUrl,
            string? apiKey,
            string? model,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            return Task.FromResult(new AiAnalysisResult(true, reply, "测试替身已回复。"));
        }
    }

    /// <summary>A share whose folders this test decides, including the ones that cannot be opened.</summary>
    private sealed class StubShareProvider(params (string FolderId, CloudListResult Result)[] folders) : ICloudProvider
    {
        private readonly Dictionary<string, CloudListResult> _folders =
            folders.ToDictionary(entry => entry.FolderId, entry => entry.Result, StringComparer.Ordinal);

        public string Id => "stub-cloud";

        public string DisplayName => "测试平台";

        public bool RequiresAuthentication => false;

        public CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

        public string? LimitationNote => "测试替身";

        public string? DocumentationUrl => null;

        public CloudAuthState GetAuthState(CloudCredential? credential) => CloudAuthState.NotRequired;

        public Task<CloudListResult> ListAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Fail(CloudErrorKind.Unsupported, "测试替身不提供网盘目录。"));

        public Task<CloudListResult> ListChildrenAsync(string folderId, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Fail(CloudErrorKind.Unsupported, "测试替身不提供网盘目录。"));

        public Task<CloudResolveResult> ResolveAsync(string shareUrl, string? fileName, CloudCredential? credential, CancellationToken cancellationToken = default)
            => Task.FromResult(CloudResolveResult.Fail(CloudErrorKind.Unsupported, "测试替身不解析下载地址。"));

        public Task<CloudListResult> ListShareFolderAsync(
            string shareUrl,
            string? parentItemId,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_folders.TryGetValue(parentItemId ?? string.Empty, out var result)
                ? result
                : CloudListResult.Fail(CloudErrorKind.NotFound, "这个目录不存在。"));
    }

    // ------------------------------------------------------- the tree the model is shown

    [Fact]
    public void The_tree_hands_over_the_real_ids_and_nothing_secret()
    {
        var json = Tree().ToPromptJson();

        Assert.Contains("1001", json);
        Assert.Contains("Tool.zip", json);
        Assert.Contains("工具/Tool.zip", json);
        Assert.Contains("folder", json);

        // A prompt is not a place for a credential, and the tree carries none.
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_truncated_tree_says_so_instead_of_looking_complete()
    {
        var tree = Tree() with { OmittedCount = 7 };

        Assert.True(tree.IsTruncated);
        Assert.Contains("\"truncated\":true", tree.ToPromptJson());
        Assert.Contains("7", tree.ToPromptJson());
    }

    // ------------------------------------------------------- what survives validation

    [Fact]
    public void A_draft_that_cites_a_real_item_is_accepted()
    {
        var validation = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"1001","platform":"Windows","architecture":"x64","confidence":0.9,"reason":"名字像安装包"}]}
            """,
            Tree());

        Assert.True(validation.HasResources);
        Assert.Empty(validation.Rejected);
        Assert.Empty(validation.Issues);

        var resource = Assert.Single(validation.Draft!.Resources);

        // Everything about the file itself comes from the share, not from the answer.
        Assert.Equal("1001", resource.CandidateId);
        Assert.Equal("Tool.zip", resource.Name);
        Assert.Equal(1024L, resource.SizeBytes);
        Assert.Equal("工具/Tool.zip", resource.Candidate.Path);
        Assert.Equal("Windows", resource.Platform);
        Assert.Equal("x64", resource.Architecture);
    }

    [Fact]
    public void A_candidate_id_that_is_not_in_the_tree_is_refused()
    {
        var validation = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"9999","platform":"Windows"}]}
            """,
            Tree());

        Assert.Null(validation.Draft);
        Assert.False(validation.HasResources);

        var rejection = Assert.Single(validation.Rejected);
        Assert.Equal("9999", rejection.Subject);
        Assert.Contains("真实资源树", rejection.Reason);
    }

    [Fact]
    public void A_resource_that_invents_a_hash_is_refused()
    {
        var validation = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"1001","sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}
            """,
            Tree());

        Assert.Null(validation.Draft);
        Assert.Contains("sha256", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void A_resource_that_invents_a_platform_or_a_link_is_refused()
    {
        var withProvider = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"1001","provider":"123"}]}
            """,
            Tree());

        Assert.Null(withProvider.Draft);
        Assert.Contains("provider", Assert.Single(withProvider.Rejected).Reason);

        var withUrl = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"1001","url":"https://example.test/file.zip"}]}
            """,
            Tree());

        Assert.Null(withUrl.Draft);
        Assert.Contains("url", Assert.Single(withUrl.Rejected).Reason);

        var withLocator = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"1001","locator":{"path":"改过的/路径.zip"}}]}
            """,
            Tree());

        Assert.Null(withLocator.Draft);
        Assert.Contains("locator", Assert.Single(withLocator.Rejected).Reason);
    }

    [Fact]
    public void A_version_nobody_verified_is_reported_and_not_used()
    {
        var validation = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"1001","version":"9.9.9"}]}
            """,
            Tree());

        // The resource is kept — losing a real file over a label would be worse — but the version
        // does not travel with it, and the fact is on the record.
        Assert.True(validation.HasResources);
        Assert.Contains("9.9.9", Assert.Single(validation.Issues).Reason);
    }

    [Fact]
    public void An_unknown_version_is_allowed_and_produces_nothing_to_report()
    {
        var validation = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"1001","version":"Unknown"}]}
            """,
            Tree());

        Assert.True(validation.HasResources);
        Assert.Empty(validation.Issues);
    }

    [Fact]
    public void A_name_or_path_the_model_changed_never_replaces_the_real_one()
    {
        var validation = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"1001","name":"Better.zip","path":"别的地方/Better.zip"}]}
            """,
            Tree());

        Assert.True(validation.HasResources);

        var resource = Assert.Single(validation.Draft!.Resources);
        Assert.Equal("Tool.zip", resource.Name);
        Assert.Equal("工具/Tool.zip", resource.Candidate.Path);

        Assert.Equal(2, validation.Issues.Count);
    }

    [Fact]
    public void The_same_item_listed_twice_is_refused_the_second_time()
    {
        var validation = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"1001"},{"candidateId":"1001"}]}
            """,
            Tree());

        Assert.Single(validation.Draft!.Resources);
        Assert.Contains("重复", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void An_answer_that_is_not_json_is_refused_without_throwing()
    {
        var validation = PluginDraftValidator.Parse("我建议你先装运行库。", Tree());

        Assert.Null(validation.Draft);
        Assert.Contains("JSON", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void A_draft_without_a_name_is_refused()
    {
        var validation = PluginDraftValidator.Parse("""{"resources":[{"candidateId":"1001"}]}""", Tree());

        Assert.Null(validation.Draft);
        Assert.Contains("插件名", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void Every_resource_refused_leaves_no_draft_to_review()
    {
        var validation = PluginDraftValidator.Parse(
            """
            {"name":"工具包","resources":[{"candidateId":"9999"},{"candidateId":"8888"}]}
            """,
            Tree());

        Assert.Null(validation.Draft);
        Assert.Equal(2, validation.Rejected.Count);
    }

    [Fact]
    public void A_model_wrapped_answer_is_still_read()
    {
        var validation = PluginDraftValidator.Parse(
            """
            好的，这是草稿：
            ```json
            {"name":"工具包","resources":[{"candidateId":"1002"}]}
            ```
            希望有帮助。
            """,
            Tree());

        Assert.True(validation.HasResources);
        Assert.True(Assert.Single(validation.Draft!.Resources).IsFolder);
    }

    /// <summary>
    /// The answer a live run actually received: a real object, real entries, and then nothing — cut off
    /// mid-string when the output limit arrived. Every object inside it was whole, so the reader used to
    /// hand back a nested entry, and the draft was refused for "没有给出插件名" — a sentence about the
    /// wrong problem, because the model had written a name and had done nothing wrong but be long.
    /// </summary>
    [Fact]
    public void An_answer_cut_off_at_the_output_limit_is_refused_as_cut_off()
    {
        var truncated =
            """
            {"name":"Windows 运行库合集","resources":[
              {"candidateId":"1001","platform":"Windows","architecture":"x64","confidence":0.9,"reason":"安装包"},
              {"candidateId":"1002","platform":"Unknown","architecture":"Unknown","confidence":0.4,"reason":"目录项，素描与色彩
            """;

        var validation = PluginDraftValidator.Parse(truncated, Tree());

        Assert.Null(validation.Draft);
        var reason = Assert.Single(validation.Rejected).Reason;
        Assert.Contains("截断", reason);
        Assert.DoesNotContain("插件名", reason);
    }

    [Fact]
    public void A_truncated_answer_wrapped_in_prose_is_still_refused_as_cut_off()
    {
        // The same failure with the prose the page's own check wraps around an answer: the object the
        // answer opens with is still the one that never closes.
        var truncated =
            "这是你要的草稿：\r\n```json\r\n"
            + """{"name":"运行库","resources":[{"candidateId":"1001"}"""
            + "\r\n```\r\n";

        var validation = PluginDraftValidator.Parse(truncated, Tree());

        Assert.Null(validation.Draft);
        Assert.Contains("截断", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void The_contract_gives_the_model_a_budget_it_can_finish_in()
    {
        // The limit is stated because an answer that never closes is worth nothing: it is the output
        // limit, not the model's ability, that decides whether a long reply is usable.
        Assert.Contains(PluginDraftValidator.MaxResources.ToString(), PluginDraftValidator.ContractHint);
        Assert.Contains("收得了尾", PluginDraftValidator.ContractHint);
        Assert.Contains("20 个字", PluginDraftValidator.ContractHint);
    }

    // ------------------------------------------------------- the capability layer

    [Fact]
    public void The_contract_tells_the_model_not_to_state_a_version()
    {
        Assert.Contains("version", PluginDraftValidator.ContractHint);
        Assert.Contains("Unknown", PluginDraftValidator.ContractHint);
        Assert.Contains("candidateId", PluginDraftValidator.ContractHint);
    }

    [Fact]
    public void The_generator_prompt_carries_the_real_tree_and_the_contract()
    {
        var capability = new PluginGeneratorCapability();
        var request = capability.Build(new AiCapabilityInput(Tree().ToPromptJson()));

        Assert.Contains("1001", request.UserPrompt);
        Assert.Contains("candidateId", request.SystemPrompt);
        Assert.Contains("Plugin Draft", request.SystemPrompt);

        // It is told, in its own words, that it may not invent a download fact.
        Assert.Contains("SHA256", request.SystemPrompt);

        // And how much of one reply it is allowed to spend, so the answer can be finished rather than
        // cut off by the output limit.
        Assert.Contains(PluginDraftValidator.MaxResources.ToString(), request.SystemPrompt);
    }

    [Fact]
    public void The_analyst_asks_for_a_plan_only_when_it_is_asked_for_one()
    {
        var capability = new EnvironmentAnalystCapability();

        var analysis = capability.Build(new AiCapabilityInput("事实清单"));
        var proposal = capability.Build(new AiCapabilityInput("事实清单", EnvironmentAnalystCapability.ProposeIntent));

        Assert.Equal(AiEnvironmentAnalyst.SystemPrompt, analysis.SystemPrompt);
        Assert.Equal(AiEnvironmentAnalyst.ProposalSystemPrompt, proposal.SystemPrompt);
        Assert.NotEqual(analysis.SystemPrompt, proposal.SystemPrompt);
    }

    [Fact]
    public void Every_registered_capability_explains_itself()
    {
        var kinds = AiCapabilities.BuiltIn.All.Select(capability => capability.Kind).ToArray();

        Assert.Contains(AiCapabilityKind.PluginGenerator, kinds);
        Assert.Contains(AiCapabilityKind.ResourceAnalyzer, kinds);
        Assert.Contains(AiCapabilityKind.EnvironmentAnalyst, kinds);
        Assert.Contains(AiCapabilityKind.Chat, kinds);

        foreach (var capability in AiCapabilities.BuiltIn.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(capability.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(capability.Description));
            Assert.Same(capability, AiCapabilities.BuiltIn.Resolve(capability.Kind));
        }

        // The same kind is never registered twice, or one of them would be unreachable.
        Assert.Equal(kinds.Length, kinds.Distinct().Count());
    }

    // ------------------------------------------------------- which service answers

    [Fact]
    public async Task A_capability_goes_to_the_service_the_user_named()
    {
        var stub = new RecordingProvider("stub-a", "{}");

        var registry = new AiProviderRegistry();
        registry.Add(stub);
        registry.Add(new RecordingProvider("stub-b", "{}"));

        var service = new AiService(registry, AiCapabilities.BuiltIn);

        var result = await service.RunAsync(
            AiCapabilityKind.PluginGenerator,
            new AiEndpointSettings("stub-b", "https://stub.invalid/v1", "m", null),
            new AiCapabilityInput(Tree().ToPromptJson()),
            new HttpClient());

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task Switching_service_needs_no_change_to_the_capability()
    {
        var first = new RecordingProvider("one", "{}");
        var second = new RecordingProvider("two", "{}");

        // Two registries, two services, the same capability object: that is the property being pinned.
        var capability = new PluginGeneratorCapability();
        var capabilities = new AiCapabilityRegistry();
        capabilities.Add(capability);

        foreach (var provider in new[] { first, second })
        {
            var providers = new AiProviderRegistry();
            providers.Add(provider);

            var service = new AiService(providers, capabilities);
            var result = await service.RunAsync(
                AiCapabilityKind.PluginGenerator,
                new AiEndpointSettings(provider.Id, "https://stub.invalid/v1", "m", null),
                new AiCapabilityInput(Tree().ToPromptJson()),
                new HttpClient());

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, provider.Calls);
            Assert.Same(capability, service.Resolve(AiCapabilityKind.PluginGenerator));
        }
    }

    [Fact]
    public async Task A_service_that_is_not_registered_is_reported_without_a_request()
    {
        var registry = new AiProviderRegistry();
        var service = new AiService(registry, AiCapabilities.BuiltIn);

        var result = await service.RunAsync(
            AiCapabilityKind.PluginGenerator,
            new AiEndpointSettings("没有这个服务", "https://stub.invalid/v1", "m", null),
            new AiCapabilityInput(Tree().ToPromptJson()),
            new HttpClient());

        Assert.False(result.Success);
        Assert.Contains("没有接入", result.Message);
    }

    [Fact]
    public async Task The_analyst_result_reaches_the_caller_unchanged()
    {
        var stub = new RecordingProvider("stub", "本机缺少 Git。");
        var registry = new AiProviderRegistry();
        registry.Add(stub);

        var service = new AiService(registry, AiCapabilities.BuiltIn);

        var result = await service.RunAsync(
            AiCapabilityKind.EnvironmentAnalyst,
            new AiEndpointSettings("stub", "https://stub.invalid/v1", "m", null),
            new AiCapabilityInput("事实清单", EnvironmentAnalystCapability.ProposeIntent),
            new HttpClient());

        Assert.True(result.Success);
        Assert.Equal("本机缺少 Git。", result.Text);
        Assert.Equal(AiEnvironmentAnalyst.ProposalSystemPrompt, stub.LastSystemPrompt);
        Assert.Equal("事实清单", stub.LastUserPrompt);
    }

    // ------------------------------------------------------- reading the real share

    [Fact]
    public async Task The_collector_reads_the_real_tree_one_folder_at_a_time()
    {
        var provider = new StubShareProvider(
            (string.Empty, CloudListResult.Ok(new[]
            {
                new CloudFile("1002", "Games", true),
                new CloudFile("1001", "Tool.zip", false, 1024L),
            })),
            ("1002", CloudListResult.Ok(new[]
            {
                new CloudFile("1003", "Setup.exe", false, 2048L),
            })));

        var collection = await PluginCandidateCollector.CollectAsync(provider, ShareUrl, credential: null);

        Assert.True(collection.Success, collection.Error);

        var tree = collection.Tree!;
        Assert.Equal("stub-cloud", tree.ProviderId);
        Assert.Equal(ShareUrl, tree.ShareUrl);
        Assert.Equal(3, tree.Items.Count);
        Assert.False(tree.IsTruncated);

        // The platform's own id is what a draft must cite, so it is what is carried.
        Assert.Equal("Tool.zip", tree.Find("1001")!.Name);
        Assert.Equal("Tool.zip", tree.Find("1001")!.Path);
        Assert.Equal("Games/Setup.exe", tree.Find("1003")!.Path);
        Assert.Equal("Games", tree.Find("1002")!.Path);
        Assert.Null(tree.Find("nope"));
    }

    [Fact]
    public async Task A_subfolder_the_platform_will_not_open_is_reported_not_hidden()
    {
        var provider = new StubShareProvider(
            (string.Empty, CloudListResult.Ok(new[]
            {
                new CloudFile("2001", "Locked", true),
                new CloudFile("2002", "Open.txt", false),
            })),
            ("2001", CloudListResult.Fail(CloudErrorKind.Forbidden, "没有权限。")));

        var collection = await PluginCandidateCollector.CollectAsync(provider, ShareUrl, credential: null);

        Assert.True(collection.Success, collection.Error);
        Assert.Equal(new[] { "Locked" }, collection.Tree!.Unreadable);
        Assert.Contains("Locked", collection.Tree.ToPromptJson());
    }

    [Fact]
    public async Task A_root_that_cannot_be_read_fails_instead_of_looking_empty()
    {
        var provider = new StubShareProvider(
            (string.Empty, CloudListResult.Fail(CloudErrorKind.AuthRequired, "需要登录。")));

        var collection = await PluginCandidateCollector.CollectAsync(provider, ShareUrl, credential: null);

        Assert.False(collection.Success);
        Assert.Null(collection.Tree);
        Assert.Equal("需要登录。", collection.Error);
    }

    [Fact]
    public async Task The_item_bound_is_applied_and_counted()
    {
        var many = Enumerable.Range(1, 10)
            .Select(index => new CloudFile($"id{index}", $"File{index}.zip", false))
            .ToArray();

        var provider = new StubShareProvider((string.Empty, CloudListResult.Ok(many)));

        var collection = await PluginCandidateCollector.CollectAsync(
            provider,
            ShareUrl,
            credential: null,
            maxDepth: 0,
            maxItems: 4);

        Assert.True(collection.Success, collection.Error);
        Assert.Equal(4, collection.Tree!.Items.Count);
        Assert.Equal(6, collection.Tree.OmittedCount);
        Assert.True(collection.Tree.IsTruncated);
    }

    [Fact]
    public async Task A_share_without_a_link_is_refused_before_any_request()
    {
        var provider = new StubShareProvider();

        var collection = await PluginCandidateCollector.CollectAsync(provider, "   ", credential: null);

        Assert.False(collection.Success);
        Assert.Contains("分享链接", collection.Error);
    }
}
