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
/// Undo and redo for the layout editor. The editor works on immutable documents,
/// so history is a stack of previous states rather than a set of inverse
/// operations that would each have to be got right.
/// </summary>
public sealed class LayoutUndoTests
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

    private static LayoutNode Node(LayoutEditorViewModel editor, string id) => LayoutEditor.Find(editor.Document, id)!;

    private static void Drag(LayoutEditorViewModel editor, string id, double dx, double dy)
    {
        var item = editor.CanvasItems.Single(i => i.Id == id);
        editor.Select(item);
        item.NudgeVisual(dx, dy);
        editor.CommitMove(item);
    }

    [Fact]
    public async Task A_new_editor_has_nothing_to_undo()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            Assert.False(editor.CanUndo);
            Assert.False(editor.CanRedo);
            Assert.Equal(0, editor.UndoDepth);

            editor.UndoCommand.Execute(null);
            Assert.Contains("没有可撤销", editor.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Undo_restores_the_geometry_from_before_the_drag()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            var before = Node(editor, "envCard").X;

            Drag(editor, "envCard", 40, 16);
            Assert.Equal(before + 40, Node(editor, "envCard").X);
            Assert.True(editor.CanUndo);

            editor.UndoCommand.Execute(null);

            Assert.Equal(before, Node(editor, "envCard").X);
            Assert.False(editor.CanUndo);
            Assert.True(editor.CanRedo);
            Assert.Contains("已撤销", editor.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Redo_reapplies_an_undone_drag()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            Drag(editor, "envCard", 40, 16);
            var moved = Node(editor, "envCard").X;

            editor.UndoCommand.Execute(null);
            editor.RedoCommand.Execute(null);

            Assert.Equal(moved, Node(editor, "envCard").X);
            Assert.True(editor.CanUndo);
            Assert.False(editor.CanRedo);
            Assert.Contains("已重做", editor.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_new_edit_discards_the_redo_stack()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            Drag(editor, "envCard", 40, 0);
            editor.UndoCommand.Execute(null);
            Assert.True(editor.CanRedo);

            // Editing after an undo forks history; the abandoned branch must go.
            Drag(editor, "envCard", 0, 30);

            Assert.False(editor.CanRedo);
            Assert.Equal(0, editor.RedoDepth);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Continuous_edits_of_the_same_property_collapse_into_one_step()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            var before = Node(editor, "envCard").X;
            editor.Select(editor.CanvasItems.Single(i => i.Id == "envCard"));

            // What a numeric box does while the user drags its spinner.
            editor.SelectedX = 100;
            editor.SelectedX = 200;
            editor.SelectedX = 300;

            Assert.Equal(300, Node(editor, "envCard").X);
            Assert.Equal(1, editor.UndoDepth);

            // One undo returns to where the run started, not to 200.
            editor.UndoCommand.Execute(null);
            Assert.Equal(before, Node(editor, "envCard").X);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Editing_a_different_property_starts_a_new_step()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            editor.Select(editor.CanvasItems.Single(i => i.Id == "envCard"));

            editor.SelectedX = 100;
            editor.SelectedY = 50;
            editor.SelectedX = 200;

            // Three separate runs: X, Y, X again.
            Assert.Equal(3, editor.UndoDepth);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Adding_and_deleting_are_separate_undoable_steps()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            var original = editor.CanvasItems.Count;

            editor.AddNodeCommand.Execute(LayoutNodeType.Text);
            Assert.Equal(original + 1, editor.CanvasItems.Count);

            editor.DeleteSelectedCommand.Execute(null);
            Assert.Equal(original, editor.CanvasItems.Count);

            editor.UndoCommand.Execute(null);
            Assert.Equal(original + 1, editor.CanvasItems.Count);

            editor.UndoCommand.Execute(null);
            Assert.Equal(original, editor.CanvasItems.Count);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Resetting_the_layout_can_be_taken_back()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            Drag(editor, "envCard", 40, 0);
            var moved = Node(editor, "envCard").X;

            // Reset is reachable by mistake, so it must not be destructive.
            editor.ResetDocumentCommand.Execute(null);
            Assert.NotEqual(moved, Node(editor, "envCard").X);
            Assert.True(editor.CanUndo);

            editor.UndoCommand.Execute(null);

            // The edit made before the reset is what comes back.
            Assert.Equal(moved, Node(editor, "envCard").X);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task History_is_bounded_so_a_long_session_does_not_grow_without_limit()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            for (var i = 0; i < 150; i++)
            {
                // A different label each time, so nothing coalesces.
                Drag(editor, "envCard", 1, 0);
            }

            Assert.True(editor.UndoDepth <= 100, $"undo depth was {editor.UndoDepth}");
            Assert.True(editor.UndoDepth > 0);
        }
        finally
        {
            Cleanup(userData);
        }
    }
}
