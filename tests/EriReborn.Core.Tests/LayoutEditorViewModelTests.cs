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
/// Drives the visual layout editor the way the view does: select, drag, resize,
/// add, hide, export and import (spec 38/39).
/// </summary>
public sealed class LayoutEditorViewModelTests
{
    private static async Task<(LayoutEditorViewModel Editor, string UserData)> CreateEditorAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = EriReborn.App.Shared.AppPaths.Detect(userDataOverride: userData);
        var network = new TestNetworkService(new HttpClient());
        var credentials = new InMemoryCredentialStore();
        var files = new TestFileSystemService(userData);

        var host = await AppHost.CreateAsync(
            paths,
            new TestPlatform(files, network, credentials),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        return (new LayoutEditorViewModel(host), userData);
    }

    [Fact]
    public async Task Applying_a_page_size_writes_it_into_the_document_as_one_step()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            var before = editor.UndoDepth;

            editor.CanvasWidth = 1200;
            editor.CanvasHeight = 800;
            editor.ApplyCanvasSizeCommand.Execute(null);

            var page = LayoutEditor.Flatten(editor.Document).Single(n => n.Type == LayoutNodeType.Page);
            Assert.Equal(1200, page.Width);
            Assert.Equal(800, page.Height);

            // One undoable step, not one per pixel the slider crossed.
            Assert.Equal(before + 1, editor.UndoDepth);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Applying_an_unchanged_size_adds_no_undo_step()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            var page = LayoutEditor.Flatten(editor.Document).Single(n => n.Type == LayoutNodeType.Page);
            editor.CanvasWidth = page.Width;
            editor.CanvasHeight = page.Height;

            var before = editor.UndoDepth;
            editor.ApplyCanvasSizeCommand.Execute(null);

            Assert.Equal(before, editor.UndoDepth);
            Assert.Contains("没有变化", editor.Status);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task An_absurd_page_size_is_clamped_rather_than_written()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            editor.CanvasWidth = 10;
            editor.CanvasHeight = 1;
            editor.ApplyCanvasSizeCommand.Execute(null);

            var page = LayoutEditor.Flatten(editor.Document).Single(n => n.Type == LayoutNodeType.Page);

            // A layout that cannot be seen is not a layout.
            Assert.Equal(320, page.Width);
            Assert.Equal(240, page.Height);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task A_page_resize_can_be_undone()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            var original = LayoutEditor.Flatten(editor.Document).Single(n => n.Type == LayoutNodeType.Page);

            editor.CanvasWidth = 1600;
            editor.CanvasHeight = 900;
            editor.ApplyCanvasSizeCommand.Execute(null);

            editor.UndoCommand.Execute(null);

            var restored = LayoutEditor.Flatten(editor.Document).Single(n => n.Type == LayoutNodeType.Page);
            Assert.Equal(original.Width, restored.Width);
            Assert.Equal(original.Height, restored.Height);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task The_page_node_frames_the_canvas_instead_of_being_a_draggable_box()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            // The Page frames the canvas rather than appearing as a box, and
            // every other node is drawn — nested ones included.
            Assert.DoesNotContain(editor.CanvasItems, i => i.Type == LayoutNodeType.Page);

            var expected = LayoutEditor.Flatten(editor.Document).Count(n => n.Type != LayoutNodeType.Page);
            Assert.Equal(expected, editor.CanvasItems.Count);

            // The canvas follows the document rather than a frozen number.
            var page = LayoutEditor.Flatten(editor.Document).Single(n => n.Type == LayoutNodeType.Page);
            Assert.Equal(page.Width, editor.CanvasWidth);
            Assert.Equal(page.Height, editor.CanvasHeight);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Dragging_updates_the_document_geometry()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            var item = editor.CanvasItems.Single(i => i.Id == "envCard");
            editor.Select(item);
            var before = LayoutEditor.Find(editor.Document, "envCard")!;

            // Simulate the view: nudge the visual, then commit.
            item.NudgeVisual(40, 16);
            editor.CommitMove(item);

            var node = LayoutEditor.Find(editor.Document, "envCard")!;

            // Relative to where it started, so the default document can change.
            Assert.Equal(before.X + 40, node.X);
            Assert.Equal(before.Y + 16, node.Y);
            Assert.Contains("已移动 envCard", editor.Status);
            Assert.Equal(item.AbsX, node.X);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Resizing_updates_the_document_size()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            var item = editor.CanvasItems.Single(i => i.Id == "envCard");
            editor.Select(item);
            var before = LayoutEditor.Find(editor.Document, "envCard")!;

            item.ResizeVisual(120, 10);
            editor.CommitResize(item);

            var node = LayoutEditor.Find(editor.Document, "envCard")!;
            Assert.Equal(before.Width + 120, node.Width);
            Assert.Equal(before.Height + 10, node.Height);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Adding_hiding_and_deleting_nodes_are_reflected_on_the_canvas()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            var before = editor.CanvasItems.Count;

            editor.AddNodeCommand.Execute(LayoutNodeType.Toggle);
            Assert.Equal(before + 1, editor.CanvasItems.Count);

            var added = editor.CanvasItems.Last();
            editor.Select(added);
            editor.ToggleSelectedVisibilityCommand.Execute(null);
            Assert.False(LayoutEditor.Find(editor.Document, added.Id)!.Visible);

            editor.DeleteSelectedCommand.Execute(null);
            Assert.Equal(before, editor.CanvasItems.Count);
            Assert.Null(LayoutEditor.Find(editor.Document, added.Id));
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Export_and_import_round_trip_through_the_editor()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            editor.Select(editor.CanvasItems.Single(i => i.Id == "envCardTitle"));
            editor.SelectedText = "新的标题";
            editor.SelectedX = 100;

            editor.ExportDocumentCommand.Execute(null);
            var exported = editor.DocumentText;
            Assert.Contains("新的标题", exported, StringComparison.Ordinal);

            editor.ResetDocumentCommand.Execute(null);
            Assert.Equal("当前环境", LayoutEditor.Find(editor.Document, "envCardTitle")!.Text);

            editor.DocumentText = exported;
            editor.ImportDocumentCommand.Execute(null);

            var restored = LayoutEditor.Find(editor.Document, "envCardTitle")!;
            Assert.Equal("新的标题", restored.Text);
            Assert.Equal(100, restored.X);
        }
        finally
        {
            Cleanup(userData);
        }
    }

    [Fact]
    public async Task Saving_and_loading_writes_the_document_to_disk()
    {
        var (editor, userData) = await CreateEditorAsync();
        try
        {
            editor.Select(editor.CanvasItems.Single(i => i.Id == "envCardTitle"));
            editor.SelectedText = "已落盘";
            editor.SaveDocumentCommand.Execute(null);

            Assert.True(File.Exists(editor.DocumentPath));
            Assert.Contains("已保存", editor.Status);

            editor.ResetDocumentCommand.Execute(null);
            editor.LoadDocumentCommand.Execute(null);

            Assert.Equal("已落盘", LayoutEditor.Find(editor.Document, "envCardTitle")!.Text);
        }
        finally
        {
            Cleanup(userData);
        }
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
}
