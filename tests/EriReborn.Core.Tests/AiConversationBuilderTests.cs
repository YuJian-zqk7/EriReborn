using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Cloud.Providers;
using EriReborn.Core.Domain;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Engine.Ai;
using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// 多轮对话式 AI 面板（spec v3.1 Task 12/13）：
/// 首轮先读真实分享树；每轮一次 completion，回复经 v2 校验后整体替换草稿树；
/// 被拒绝的回复不动草稿、原因留在对话里；可重试上一轮、换链接重建会话；
/// 草稿树勾选后合并进插件树，重复应用幂等不重复插入。
/// </summary>
public sealed class AiConversationBuilderTests : IDisposable
{
    private const string ShareUrl = "https://www.123pan.com/s/abc";
    private const string AiId = "stub-tree-ai";

    private const string TurnOneJson =
        """
        {"name":"工具集","nodes":[{"nodeType":"group","name":"工具","children":[
          {"nodeType":"file","candidateId":"file-A","platform":"Windows","architecture":"x64","confidence":0.9,"reason":"zip 工具"}
        ]}]}
        """;

    private const string TurnTwoJson =
        """
        {"name":"工具集","nodes":[{"nodeType":"group","name":"全部","children":[
          {"nodeType":"file","candidateId":"file-A"},
          {"nodeType":"file","candidateId":"file-B"}
        ]}]}
        """;

    private const string BadTurnJson =
        """
        {"name":"幻影","nodes":[{"nodeType":"file","candidateId":"ghost","platform":"Windows"}]}
        """;

    private readonly string _userData;
    private readonly PluginBuilderViewModel _builder;
    private readonly ScriptedAiProvider _ai;

    public AiConversationBuilderTests()
    {
        _userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: _userData);

        _ai = new ScriptedAiProvider(AiId);
        AiProviders.BuiltIn.Add(_ai);

        var cloud = new FakeTreeCloudProvider();
        var host = AppHost.CreateAsync(
            paths,
            new TestPlatform(
                new TestFileSystemService(_userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(new ICloudProvider[] { cloud }, AppLog.For("Test")),
            AppLog.For("Test")).GetAwaiter().GetResult();

        host.UserConfig.Save(host.UserConfig.Current with
        {
            AiProviderId = AiId,
            AiBaseUrl = "https://stub.invalid/v1",
            AiModel = "stub-model",
        });

        _builder = new PluginBuilderViewModel(host) { AiShareUrl = ShareUrl };
    }

    private Task SendAsync(string instruction)
    {
        _builder.AiInput = instruction;
        return _builder.SendAiMessageCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task First_reads_the_real_share_then_replaces_the_whole_draft_tree_each_turn()
    {
        _ai.Enqueue(TurnOneJson);
        _ai.Enqueue(TurnTwoJson);

        await SendAsync("先只放 file-A 到工具组");

        // 系统契约必须是 v2 多轮契约；用户消息先给真实树（含嵌套文件夹的真实 id），再给指令。
        Assert.Equal(PluginTreeDraftValidator.SystemPrompt, _ai.LastSystemPrompt);
        Assert.Contains("【真实分享树 JSON】", _ai.LastUserPrompt);
        Assert.Contains("folder-a1", _ai.LastUserPrompt);
        Assert.Contains("file-A", _ai.LastUserPrompt);

        var group = Assert.Single(_builder.AiDraftTreeRoots);
        Assert.Equal("工具", group.Name);
        Assert.True(group.IsGroup);
        var first = Assert.Single(group.Children);
        Assert.Equal("file-A", first.CandidateId);
        Assert.Equal("工具集", _builder.AiDraftName);
        Assert.Contains(_builder.AiMessages, message => message.IsUser);
        Assert.Contains(_builder.AiMessages, message => message.IsAssistant);

        await SendAsync("两个文件都放进全部组");

        // 第二轮带历史；草稿树是整体替换：旧分组「工具」消失，新分组「全部」带两个文件。
        Assert.Equal(2, _ai.Calls);
        Assert.Contains("【历史对话】", _ai.LastUserPrompt);
        Assert.Contains("【当前草稿树 JSON】", _ai.LastUserPrompt);

        group = Assert.Single(_builder.AiDraftTreeRoots);
        Assert.Equal("全部", group.Name);
        Assert.Equal(2, group.Children.Count);
        Assert.Contains(group.Children, row => row.CandidateId == "file-A");
        Assert.Contains(group.Children, row => row.CandidateId == "file-B");
    }

    [Fact]
    public async Task A_rejected_turn_keeps_the_previous_draft_and_explains_itself()
    {
        _ai.Enqueue(TurnOneJson);
        await SendAsync("整理成一组");

        _ai.Enqueue(BadTurnJson);
        await SendAsync("胡说八道一轮");

        // 幽灵 id 不在真实树里：整份回复不采纳，草稿树维持上一轮。
        var group = Assert.Single(_builder.AiDraftTreeRoots);
        Assert.Equal("工具", group.Name);
        Assert.Single(group.Children);
        var lastAssistant = _builder.AiMessages.Last(message => message.IsAssistant);
        Assert.Contains("没有通过校验", lastAssistant.Text);
        Assert.Contains("ghost", lastAssistant.Text);
    }

    [Fact]
    public async Task Retry_re_runs_the_last_instruction_after_a_failed_turn()
    {
        _ai.Enqueue(BadTurnJson);
        _ai.Enqueue(TurnOneJson);

        await SendAsync("先胡说八道一轮");
        Assert.Empty(_builder.AiDraftTreeRoots);

        await _builder.RetryAiTurnCommand.ExecuteAsync(null);

        Assert.Equal(2, _ai.Calls);
        var group = Assert.Single(_builder.AiDraftTreeRoots);
        Assert.Equal("工具", group.Name);
        Assert.Single(group.Children);

        // 撤掉了失败轮的用户消息后重发，界面上同一条指令只出现一次。
        Assert.Single(_builder.AiMessages, message => message.IsUser);
        Assert.Contains(_builder.AiMessages, message => message.IsAssistant && message.Text.Contains("草稿树已更新"));
    }

    [Fact]
    public async Task Changing_the_share_url_resets_the_session_and_draft_tree()
    {
        _ai.Enqueue(TurnOneJson);
        await SendAsync("整理成一组");
        Assert.NotEmpty(_builder.AiDraftTreeRoots);
        Assert.NotEmpty(_builder.AiMessages);

        _builder.AiShareUrl = "https://pan.quark.cn/s/other";

        Assert.Empty(_builder.AiDraftTreeRoots);
        Assert.Empty(_builder.AiMessages);
        Assert.False(_builder.HasAiDraftTree);
        Assert.Equal(string.Empty, _builder.AiDraftName);
    }

    [Fact]
    public async Task Applying_checked_subtrees_is_idempotent_and_skips_unchecked_nodes()
    {
        _ai.Enqueue(TurnTwoJson);
        await SendAsync("两个文件都放进全部组");

        _builder.ApplyAiDraftCommand.Execute(null);

        var root = Assert.Single(_builder.Nodes);
        Assert.True(root.IsGroup);
        Assert.Equal("全部", root.Name);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(2, _builder.Resources.Count);

        var fileA = root.Children.Single(node => node.Resource!.Source.Locator!.ProviderItemId == "file-A");
        Assert.Equal(SourceKind.CloudShare, fileA.Resource!.Source.Kind);
        Assert.Equal(ShareUrl, fileA.Resource.Source.ShareUrl);
        Assert.Equal(ResourceLocatorKind.File, fileA.Resource.Source.Locator!.Kind);
        Assert.Equal("ResourceA.zip", fileA.Resource.Name);

        // 再来一次：分组按名字复用、资源按候选 id 去重，不产生第二份。
        _builder.ApplyAiDraftCommand.Execute(null);
        root = Assert.Single(_builder.Nodes);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(2, _builder.Resources.Count);
        Assert.Contains("跳过", _builder.AiStatus);

        // 清空插件树后取消勾选 file-B 再应用：未勾选子树不进入插件树，也不留下空结构。
        _builder.ResetCommand.Execute(null);
        var current = Assert.Single(_builder.AiDraftTreeRoots);
        var rowB = Assert.Single(current.Children, row => row.CandidateId == "file-B");
        rowB.IsSelected = false;

        _builder.ApplyAiDraftCommand.Execute(null);
        root = Assert.Single(_builder.Nodes);
        Assert.Single(root.Children);
        Assert.Equal("file-A", root.Children[0].Resource!.Source.Locator!.ProviderItemId);
    }

    [Fact]
    public async Task Ungroup_promotes_children_and_convert_to_group_drops_the_binding()
    {
        _ai.Enqueue(TurnTwoJson);
        await SendAsync("两个文件都放进全部组");

        // 取消分组：两个文件提升到根层。
        var group = Assert.Single(_builder.AiDraftTreeRoots);
        _builder.UngroupAiDraftNodeCommand.Execute(group);

        Assert.Equal(2, _builder.AiDraftTreeRoots.Count);
        Assert.All(_builder.AiDraftTreeRoots, row => Assert.Null(row.Parent));
        Assert.All(_builder.AiDraftTreeRoots, row => Assert.True(row.IsResource));

        // 把其中一个文件改成纯分组：它不再产生下载节点，另一个文件照常应用。
        var fileRow = Assert.Single(_builder.AiDraftTreeRoots, row => row.CandidateId == "file-A");
        _builder.ConvertAiDraftNodeToGroupCommand.Execute(fileRow);

        var converted = Assert.Single(_builder.AiDraftTreeRoots, row => row.Name == "ResourceA.zip");
        Assert.True(converted.IsGroup);

        _builder.ApplyAiDraftCommand.Execute(null);

        var applied = Assert.Single(_builder.Nodes);
        Assert.False(applied.IsGroup);
        Assert.Equal("file-B", applied.Resource!.Source.Locator!.ProviderItemId);
    }

    public void Dispose()
    {
        AiProviders.BuiltIn.Remove(AiId);
        try
        {
            if (Directory.Exists(_userData))
            {
                Directory.Delete(_userData, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>按队列逐轮返回预设回复的 AI 替身，并记录收到的契约与提示。</summary>
    private sealed class ScriptedAiProvider : IAiProvider
    {
        private readonly Queue<string> _replies = new();

        public ScriptedAiProvider(string id) => Id = id;

        public int Calls { get; private set; }

        public string? LastSystemPrompt { get; private set; }

        public string? LastUserPrompt { get; private set; }

        public string Id { get; }

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

        public void Enqueue(string reply) => _replies.Enqueue(reply);

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
            var reply = _replies.Dequeue();
            return Task.FromResult(new AiAnalysisResult(true, reply, "测试替身已回复。"));
        }
    }

    /// <summary>两层文件夹 + 两个文件的 123 分享：A/ → a1/ → ResourceA.zip, ResourceB.zip。</summary>
    private sealed class FakeTreeCloudProvider : CloudProviderBase
    {
        public FakeTreeCloudProvider()
            : base(new TestNetworkService(new HttpClient()), new InMemoryCredentialStore())
        {
        }

        public override string Id => CloudProviderIds.Pan123;

        public override string DisplayName => "123 云盘（测试）";

        public override bool RequiresAuthentication => false;

        public override CloudImplementationKind ImplementationKind => CloudImplementationKind.OfficialApi;

        public override Task<CloudListResult> ListShareFolderAsync(
            string shareUrl,
            string? parentItemId,
            CloudCredential? credential,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CloudListResult.Ok(parentItemId switch
            {
                null => new[] { new CloudFile("folder-A", "A", true) },
                "folder-A" => new[] { new CloudFile("folder-a1", "a1", true) },
                "folder-a1" => new[]
                {
                    new CloudFile("file-A", "ResourceA.zip", false, 512),
                    new CloudFile("file-B", "ResourceB.zip", false, 1024),
                },
                _ => Array.Empty<CloudFile>(),
            }));
    }
}
