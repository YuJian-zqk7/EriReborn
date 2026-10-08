using EriReborn.App.Shared;
using EriReborn.App.Shared.Services;
using EriReborn.App.Shared.ViewModels;
using EriReborn.Cloud;
using EriReborn.Core.Logging;
using EriReborn.Core.Tests.TestSupport;
using EriReborn.Layout;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Stack and Grid only arrange their children, and nothing could previously put
/// a node inside one — so the two types had no observable behaviour at all. This
/// covers the whole chain: nest, arrange, refuse to drag, and report overflow.
/// </summary>
public sealed class LayoutNestingTests
{
    private static async Task<(LayoutEditorViewModel Editor, string UserData)> CreateEditorAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);

        var host = await AppHost.CreateAsync(
            paths,
            new TestPlatform(
                new TestFileSystemService(userData),
                new TestNetworkService(new HttpClient()),
                new InMemoryCredentialStore()),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        return (new LayoutEditorViewModel(host), userData);
    }

    private static void Cleanup(string userData)
    {
        try
        {
            if (Directory.Exists(userData))
            {
                Directory.Delete(userData, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static LayoutNode Leaf(string id, double x = 0, double y = 0)
        => new() { Id = id, Type = LayoutNodeType.Button, X = x, Y = y, Width = 100, Height = 20 };

    [Fact]
    public void Reparenting_puts_a_node_under_the_container()
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            Width = 200,
            Height = 200,
            Children = Array.Empty<LayoutNode>(),
        };

        var document = new LayoutDocument { Page = "Test", Nodes = new LayoutNode[] { stack, Leaf("a", 50, 50) } };

        var updated = LayoutEditor.Reparent(document, "a", "s");

        Assert.Contains(LayoutEditor.Find(updated, "s")!.Children, n => n.Id == "a");
        Assert.DoesNotContain(updated.Nodes, n => n.Id == "a");
        Assert.True(LayoutFlow.IsManagedByParent(updated, "a"));
    }

    [Fact]
    public void Reparenting_into_own_descendant_is_refused()
    {
        var inner = new LayoutNode
        {
            Id = "inner",
            Type = LayoutNodeType.Container,
            Width = 100,
            Height = 100,
            Children = Array.Empty<LayoutNode>(),
        };

        var outer = new LayoutNode
        {
            Id = "outer",
            Type = LayoutNodeType.Container,
            Width = 200,
            Height = 200,
            Children = new[] { inner },
        };

        var document = new LayoutDocument { Page = "Test", Nodes = new LayoutNode[] { outer } };

        // Moving outer inside inner would detach the whole subtree.
        Assert.Same(document, LayoutEditor.Reparent(document, "outer", "inner"));
    }

    [Fact]
    public void MoveToRoot_takes_a_node_back_out()
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            Width = 200,
            Height = 200,
            Children = new[] { Leaf("a") },
        };

        var document = new LayoutDocument { Page = "Test", Nodes = new LayoutNode[] { stack } };

        var updated = LayoutEditor.MoveToRoot(document, "a");

        Assert.Contains(updated.Nodes, n => n.Id == "a");
        Assert.False(LayoutFlow.IsManagedByParent(updated, "a"));
    }

    [Fact]
    public async Task Moving_a_node_into_a_container_makes_it_arranged_on_the_canvas()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            // The default document has a Grid called "stats" with no children.
            editor.Select(editor.CanvasItems.Single(i => i.Id == "envCard"));

            editor.MoveIntoContainerCommand.Execute(null);

            var item = editor.CanvasItems.Single(i => i.Id == "envCard");
            Assert.True(item.IsManaged);
            Assert.Contains("移入", editor.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Dragging_an_arranged_node_pulls_it_out_of_the_container()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            editor.Select(editor.CanvasItems.Single(i => i.Id == "envCard"));
            editor.MoveIntoContainerCommand.Execute(null);

            var item = editor.CanvasItems.Single(i => i.Id == "envCard");
            editor.Select(item);
            Assert.True(item.IsManaged);

            // Dragging used to be refused outright, which read as "拖不动". The drag is
            // a deliberate gesture, so it now detaches the element and drops it where
            // the pointer left it.
            item.NudgeVisual(30, 30);
            editor.CommitMove(item);

            Assert.False(LayoutFlow.IsManagedByParent(editor.Document, "envCard"));
            Assert.Contains(editor.CanvasItems, i => i.Id == "envCard" && !i.IsManaged);
            Assert.Contains("移出", editor.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Clicking_an_arranged_node_selects_without_editing_it()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            editor.Select(editor.CanvasItems.Single(i => i.Id == "envCard"));
            editor.MoveIntoContainerCommand.Execute(null);

            var before = editor.Document;
            var item = editor.CanvasItems.Single(i => i.Id == "envCard");
            editor.Select(item);

            // A click is a commit with no movement: it must never restructure anything.
            editor.CommitMove(item);

            Assert.Same(before, editor.Document);
            Assert.True(LayoutFlow.IsManagedByParent(editor.Document, "envCard"));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Moving_a_node_back_out_makes_it_draggable_again()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            editor.Select(editor.CanvasItems.Single(i => i.Id == "envCard"));
            editor.MoveIntoContainerCommand.Execute(null);

            var managed = editor.CanvasItems.Single(i => i.Id == "envCard");
            editor.Select(managed);
            editor.MoveOutOfContainerCommand.Execute(null);

            var freed = editor.CanvasItems.Single(i => i.Id == "envCard");
            Assert.False(freed.IsManaged);
            Assert.Contains("移出", editor.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public void Content_larger_than_its_container_is_reported_not_clipped_silently()
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            Width = 200,
            Height = 40, // room for one child, not three
            Properties = new Dictionary<string, string> { ["orientation"] = "vertical" },
            Children = new[] { Leaf("a"), Leaf("b"), Leaf("c") },
        };

        var document = new LayoutDocument { Page = "Test", Nodes = new LayoutNode[] { stack } };

        var result = LayoutValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Code == "layout.overflow");
    }

    [Fact]
    public void A_container_that_fits_its_children_is_not_reported()
    {
        var stack = new LayoutNode
        {
            Id = "s",
            Type = LayoutNodeType.Stack,
            Width = 200,
            Height = 120,
            Properties = new Dictionary<string, string> { ["orientation"] = "vertical" },
            Children = new[] { Leaf("a"), Leaf("b") },
        };

        var document = new LayoutDocument { Page = "Test", Nodes = new LayoutNode[] { stack } };

        Assert.DoesNotContain(LayoutValidator.Validate(document).Issues, i => i.Code == "layout.overflow");
    }
}
