using EriReborn.Engine.Plugins;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// v2 generator contract (spec v3.1, AC-9/AC-10): the candidate list nests into a real tree, and a
/// model answer is validated as a tree — fabricated ids, type mismatches, forbidden download facts,
/// duplicates and over-limit subtrees are refused with a reason, never silently corrected.
/// </summary>
public sealed class DraftTreeValidatorTests
{
    private const string ShareUrl = "https://share.example.test/s/abc";

    // Flat BFS-shaped list, exactly like PluginCandidateCollector emits:
    //  A/            (folder 2000)
    //  A/a1/         (folder 2001)
    //  A/a1/Tool.zip (file   1001)
    //  A/Data.bin    (file   1002)
    //  Readme.txt    (file   1003, share root)
    private static PluginCandidateTree Tree() => new(
        "123",
        ShareUrl,
        new[]
        {
            new PluginCandidate("2000", "A", true, null, "A"),
            new PluginCandidate("2001", "a1", true, null, "A/a1"),
            new PluginCandidate("1001", "Tool.zip", false, 1024L, "A/a1/Tool.zip"),
            new PluginCandidate("1002", "Data.bin", false, 2048L, "A/Data.bin"),
            new PluginCandidate("1003", "Readme.txt", false, 16L, "Readme.txt"),
        });

    // ------------------------------------------------------- TR-11.2 nesting from flat

    [Fact]
    public void CandidateTree_Nesting_FromFlat_keeps_every_id_path_and_kind()
    {
        var roots = Tree().Roots;

        // Two roots: folder A and the root file.
        Assert.Equal(2, roots.Count);

        var folderA = Assert.IsType<PluginCandidateNode>(roots[0]);
        Assert.Equal("2000", folderA.CandidateId);
        Assert.True(folderA.IsFolder);
        Assert.Equal("A", folderA.Path);

        // A contains the subfolder a1 and the sibling file Data.bin in flat order.
        Assert.Equal(2, folderA.Children.Count);
        var a1 = Assert.Single(folderA.Children, c => c.CandidateId == "2001");
        Assert.Equal("a1", a1.Name);
        Assert.True(a1.IsFolder);

        var deepFile = Assert.Single(a1.Children);
        Assert.Equal("1001", deepFile.CandidateId);
        Assert.False(deepFile.IsFolder);
        Assert.Equal("A/a1/Tool.zip", deepFile.Path);
        Assert.Empty(deepFile.Children);

        var dataFile = Assert.Single(folderA.Children, c => c.CandidateId == "1002");
        Assert.Equal("Data.bin", dataFile.Name);

        Assert.Equal("Readme.txt", roots[1].Name);

        // No item lost: flattening the nested view must cover every flat item exactly once.
        var flat = Tree().Roots.SelectMany(r => r.EnumerateSelfAndDescendants()).ToList();
        Assert.Equal(Tree().Items.Count, flat.Count);
        Assert.Equal(
            Tree().Items.Select(i => i.CandidateId).OrderBy(id => id, StringComparer.Ordinal),
            flat.Select(n => n.CandidateId).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void CandidateTree_Nesting_tolerates_a_missing_intermediate_folder()
    {
        // Flat list mentions a deep file without listing the parent folders themselves. The nested
        // view still has to carry the file (id falls back to the path), never drop it.
        var tree = new PluginCandidateTree(
            "123",
            ShareUrl,
            new[] { new PluginCandidate("X-y-f", "f.zip", false, 1L, "X/y/f.zip") });

        var root = Assert.Single(tree.Roots);
        Assert.Equal("X", root.Name);
        Assert.True(root.IsFolder);

        var y = Assert.Single(root.Children);
        Assert.Equal("y", y.Name);

        var file = Assert.Single(y.Children);
        Assert.Equal("X-y-f", file.CandidateId);
        Assert.Equal("f.zip", file.Name);
    }

    [Fact]
    public void The_prompt_json_is_nested_and_keeps_the_truncation_declaration()
    {
        var json = Tree().ToPromptJson();

        Assert.Contains("\"nodes\":", json);
        Assert.Contains("\"children\":", json);
        Assert.Contains("A/a1/Tool.zip", json);
        Assert.Contains("\"type\":\"folder\"", json);
        Assert.DoesNotContain("\"items\":", json, StringComparison.Ordinal);

        var truncated = Tree() with { OmittedCount = 7 };
        Assert.Contains("\"truncated\":true", truncated.ToPromptJson());
    }

    // ------------------------------------------------------- TR-11.1 validator: accepted

    [Fact]
    public void A_legal_two_level_tree_is_accepted_with_its_annotations()
    {
        var json = """
        {
          "name": "工具合集",
          "nodes": [
            {"nodeType":"group","name":"客户端","children":[
              {"nodeType":"file","candidateId":"1001","platform":"Windows","architecture":"x64","confidence":0.9,"reason":"安装包"},
              {"nodeType":"folder","candidateId":"2001","reason":"整包配置"}
            ]},
            {"nodeType":"file","candidateId":"1003","platform":"Unknown","architecture":"Unknown","confidence":0.2,"reason":"说明"}
          ]
        }
        """;

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        Assert.True(result.HasNodes);
        Assert.Empty(result.Rejected);
        Assert.Empty(result.Issues);

        var draft = result.Draft!;
        Assert.Equal("工具合集", draft.Name);
        Assert.Equal(2, draft.Nodes.Count);

        var group = Assert.IsType<DraftGroupNode>(draft.Nodes[0]);
        Assert.Equal("客户端", group.Name);
        Assert.Equal(2, group.Children.Count);

        var file = Assert.IsType<DraftResourceNode>(group.Children[0]);
        Assert.Equal("file", file.NodeType);
        Assert.Equal("1001", file.CandidateId);
        Assert.Equal("Windows", file.Platform);
        Assert.Equal("x64", file.Architecture);

        var folder = Assert.IsType<DraftResourceNode>(group.Children[1]);
        Assert.Equal("folder", folder.NodeType);
        Assert.Equal("2001", folder.CandidateId);
        Assert.True(folder.IsFolder);
    }

    // ------------------------------------------------------- refused nodes

    [Fact]
    public void A_fabricated_candidate_id_is_refused_while_siblings_survive()
    {
        var json = """
        {"name":"草稿","nodes":[
          {"nodeType":"file","candidateId":"1001"},
          {"nodeType":"file","candidateId":"does-not-exist"}
        ]}
        """;

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        Assert.True(result.HasNodes);
        Assert.Single(result.Draft!.Nodes);
        var rejection = Assert.Single(result.Rejected);
        Assert.Equal("does-not-exist", rejection.Subject);
        Assert.Contains("没有这个 candidateId", rejection.Reason);
    }

    [Theory]
    [InlineData("folder", "1001")] // real file labelled as a folder
    [InlineData("file", "2001")]   // real folder labelled as a file
    public void A_type_mismatch_refuses_the_whole_node(string stated, string candidateId)
    {
        var json = $"{{\"name\":\"草稿\",\"nodes\":[{{\"nodeType\":\"{stated}\",\"candidateId\":\"{candidateId}\"}}]}}";

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        Assert.False(result.HasNodes);
        var rejection = Assert.Single(result.Rejected);
        Assert.Equal(candidateId, rejection.Subject);
        Assert.Contains("类型不匹配", rejection.Reason);
    }

    [Fact]
    public void The_same_candidate_id_cannot_appear_twice_anywhere_in_the_tree()
    {
        var json = """
        {"name":"草稿","nodes":[
          {"nodeType":"group","name":"一","children":[{"nodeType":"file","candidateId":"1001"}]},
          {"nodeType":"group","name":"二","children":[{"nodeType":"file","candidateId":"1001"}]}
        ]}
        """;

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        // The second 1001 is refused even though it is correctly typed and nested elsewhere.
        var duplicate = Assert.Single(result.Rejected);
        Assert.Equal("1001", duplicate.Subject);
        Assert.Contains("重复", duplicate.Reason);

        var allFiles = result.Draft!.EnumerateAll().OfType<DraftResourceNode>().ToList();
        Assert.Single(allFiles);
    }

    [Theory]
    [InlineData("sha256", "sha256")]
    [InlineData("url", "url")]
    [InlineData("locator", "locator")]
    [InlineData("shareurl", "shareurl")]
    public void A_forbidden_download_fact_refuses_the_resource_node(string field, string expectedFragment)
    {
        var json = $"{{\"name\":\"草稿\",\"nodes\":[{{\"nodeType\":\"file\",\"candidateId\":\"1001\",\"{field}\":\"made-up\"}}]}}";

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        Assert.False(result.HasNodes);
        Assert.Contains(expectedFragment, Assert.Single(result.Rejected).Reason);
    }

    [Fact]
    public void An_unknown_node_type_is_refused_with_its_subtree()
    {
        var json = """
        {"name":"草稿","nodes":[
          {"nodeType":"category","name":"X","children":[{"nodeType":"file","candidateId":"1001"}]}
        ]}
        """;

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        Assert.False(result.HasNodes);
        Assert.Single(result.Rejected);
        Assert.Contains("nodeType", result.Rejected[0].Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void A_group_without_a_plain_name_is_refused(string groupName)
    {
        var escaped = groupName.Replace("\\", "\\\\");
        var json = $"{{\"name\":\"草稿\",\"nodes\":[{{\"nodeType\":\"group\",\"name\":\"{escaped}\",\"children\":[{{\"nodeType\":\"file\",\"candidateId\":\"1001\"}}]}}]}}";

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        Assert.False(result.HasNodes);
        Assert.Single(result.Rejected);
        // The valid file beneath the malformed group does not sneak out on its own.
        Assert.Null(result.Draft);
    }

    [Fact]
    public void Children_on_a_file_node_are_dropped_but_the_file_survives()
    {
        var json = """
        {"name":"草稿","nodes":[
          {"nodeType":"file","candidateId":"1001","children":[{"nodeType":"file","candidateId":"1003"}]}
        ]}
        """;

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        var file = Assert.IsType<DraftResourceNode>(Assert.Single(result.Draft!.Nodes));
        Assert.Empty(file.Children);
        Assert.Contains("children", Assert.Single(result.Issues).Reason);
        Assert.Empty(result.Rejected);
    }

    // ------------------------------------------------------- walls & shape failures

    [Fact]
    public void Nested_groups_deeper_than_the_depth_wall_are_refused()
    {
        // 13 nested groups: depth 1..12 accepted, the 13th is beyond MaxDepth and refused.
        var json = BuildGroupChain(PluginTreeDraftValidator.MaxDepth + 1);

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        Assert.True(result.HasNodes);
        Assert.Single(result.Rejected);
        Assert.Contains("12", result.Rejected[0].Reason);

        var depth = 0;
        var node = result.Draft!.Nodes[0];
        while (true)
        {
            depth++;
            if (node.Children.Count == 0)
            {
                break;
            }

            node = node.Children[0];
        }

        Assert.Equal(PluginTreeDraftValidator.MaxDepth, depth);
    }

    [Fact]
    public void More_nodes_than_the_node_wall_are_refused_at_the_wall()
    {
        var groups = string.Join(
            ",",
            Enumerable.Range(0, PluginTreeDraftValidator.MaxNodes + 1)
                .Select(i => $"{{\"nodeType\":\"group\",\"name\":\"g{i}\"}}"));
        var json = $"{{\"name\":\"草稿\",\"nodes\":[{groups}]}}";

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        Assert.Equal(PluginTreeDraftValidator.MaxNodes, result.Draft!.Nodes.Count);
        Assert.Single(result.Rejected);
        Assert.Contains(PluginTreeDraftValidator.MaxNodes.ToString(), result.Rejected[0].Reason);
    }

    [Fact]
    public void Bad_json_and_a_missing_name_or_nodes_array_fail_the_whole_draft()
    {
        Assert.Contains("JSON", PluginTreeDraftValidator.Parse("{not json", Tree()).Rejected[0].Reason);
        Assert.Contains("插件名", PluginTreeDraftValidator.Parse("""{"nodes":[]}""", Tree()).Rejected[0].Reason);
        Assert.Contains("nodes", PluginTreeDraftValidator.Parse("""{"name":"x"}""", Tree()).Rejected[0].Reason);
        Assert.False(PluginTreeDraftValidator.Parse("   ", Tree()).HasNodes);
    }

    [Fact]
    public void A_cut_off_answer_is_refused_with_the_shorten_and_retry_message()
    {
        var cut =
            "```json\r\n"
            + """{"name":"运行库","nodes":[{"nodeType":"file","candidateId":"1001"}"""
            + "\r\n```";

        var result = PluginTreeDraftValidator.Parse(cut, Tree());

        Assert.Null(result.Draft);
        Assert.Contains("截断", Assert.Single(result.Rejected).Reason);
    }

    // ------------------------------------------------------- field-level issues, not refusals

    [Fact]
    public void Wrong_name_path_and_version_fields_are_reported_but_the_node_survives()
    {
        var json = """
        {"name":"草稿","nodes":[
          {"nodeType":"file","candidateId":"1001","name":"假名字.zip","path":"假路径","version":"9.9"}
        ]}
        """;

        var result = PluginTreeDraftValidator.Parse(json, Tree());

        var file = Assert.IsType<DraftResourceNode>(Assert.Single(result.Draft!.Nodes));
        Assert.Equal("Tool.zip", file.Name);
        Assert.Equal(3, result.Issues.Count);
        Assert.Empty(result.Rejected);
    }

    [Fact]
    public void The_contract_states_every_wall_and_the_no_invention_rules()
    {
        var hint = PluginTreeDraftValidator.ContractHint;

        Assert.Contains(PluginTreeDraftValidator.MaxDepth.ToString(), hint);
        Assert.Contains(PluginTreeDraftValidator.MaxNodes.ToString(), hint);
        Assert.Contains("nodeType", hint);
        Assert.Contains("candidateId", hint);
        Assert.Contains("sha256", hint);
        Assert.Contains("收得了尾", hint);
    }

    /// <summary>Builds one root group chain <paramref name="depth"/> levels deep, innermost empty.</summary>
    private static string BuildGroupChain(int depth)
    {
        var open = string.Concat(Enumerable.Range(1, depth)
            .Select(i => $"{{\"nodeType\":\"group\",\"name\":\"g{i}\",\"children\":["));
        var close = string.Concat(Enumerable.Repeat("]}", depth));
        return $"{{\"name\":\"草稿\",\"nodes\":[{open}{close}]}}";
    }
}
