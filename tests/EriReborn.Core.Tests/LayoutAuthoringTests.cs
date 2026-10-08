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
/// The parts of the layout editor that make an added component worth adding: naming it, giving it a
/// shape, a link and a picture. A component that could only be a grey box was the reason "adding a
/// component" looked pointless.
/// </summary>
public sealed class LayoutAuthoringTests
{
    private static async Task<(LayoutEditorViewModel Editor, AppHost Host)> CreateEditorAsync()
    {
        var userData = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        var paths = AppPaths.Detect(userDataOverride: userData);
        var network = new TestNetworkService(new HttpClient());
        var credentials = new InMemoryCredentialStore();
        var files = new TestFileSystemService(userData);

        var host = await AppHost.CreateAsync(
            paths,
            new TestPlatform(files, network, credentials),
            new CloudProviderRegistry(Array.Empty<ICloudProvider>(), AppLog.For("Test")),
            AppLog.For("Test"));

        return (new LayoutEditorViewModel(host), host);
    }

    private static LayoutEditorItem SelectFirst(LayoutEditorViewModel editor)
    {
        var item = editor.CanvasItems.First();
        editor.Select(item);
        return item;
    }

    [Fact]
    public async Task The_palette_names_components_in_words_a_user_reads()
    {
        var (editor, _) = await CreateEditorAsync();

        // The palette used to show the enum names — Container, Stack, Toggle — which is the API talking.
        Assert.DoesNotContain(editor.Palette, item => string.Equals(item.Name, item.Type.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(editor.Palette, item => item.Type == LayoutNodeType.Page);
        Assert.Contains(editor.Palette, item => item.Type == LayoutNodeType.Image && item.Name == "图片");

        // And the entry still adds the component it names.
        var before = LayoutEditor.Flatten(editor.Document).Count();
        editor.AddNodeCommand.Execute(editor.Palette.First(item => item.Type == LayoutNodeType.Image).Type);
        Assert.Equal(before + 1, LayoutEditor.Flatten(editor.Document).Count());
    }

    [Fact]
    public async Task A_node_can_be_renamed_and_a_taken_name_is_refused()
    {
        var (editor, _) = await CreateEditorAsync();
        var item = SelectFirst(editor);
        var original = item.Id;
        var other = editor.CanvasItems.First(other => other.Id != original).Id;

        editor.SelectedName = "首页主标题";
        Assert.Equal("首页主标题", editor.SelectedItem!.Id);
        Assert.Contains(LayoutEditor.Flatten(editor.Document), node => node.Id == "首页主标题");

        // Two elements with one name would make the document refer to whichever came first, so the
        // rename is refused and said out loud rather than silently merging them.
        var before = editor.Document;
        editor.SelectedName = other;
        Assert.Same(before, editor.Document);
        Assert.Equal("首页主标题", editor.SelectedItem!.Id);
        Assert.Contains("已经被另一个元素用了", editor.Status, StringComparison.Ordinal);

        // Blank is not a name either.
        editor.SelectedName = "   ";
        Assert.Equal("首页主标题", editor.SelectedItem!.Id);
    }

    [Fact]
    public async Task Renaming_is_one_undoable_step()
    {
        var (editor, _) = await CreateEditorAsync();
        var item = SelectFirst(editor);
        var original = item.Id;

        editor.SelectedName = "改过的名字";
        Assert.Equal(1, editor.UndoDepth);

        editor.UndoCommand.Execute(null);
        Assert.Contains(LayoutEditor.Flatten(editor.Document), node => node.Id == original);
    }

    [Fact]
    public async Task A_page_can_be_named()
    {
        var (editor, _) = await CreateEditorAsync();

        editor.PageName = "Overview";
        Assert.Equal("Overview", editor.Document.Page);
        Assert.Equal("Overview", editor.PageName);
    }

    [Fact]
    public async Task Shape_link_and_picture_are_written_into_the_node_and_survive_a_save()
    {
        var (editor, host) = await CreateEditorAsync();
        SelectFirst(editor);

        editor.SelectedShapeChoice = editor.ShapeChoices.First(choice => choice.Value == "circle");
        editor.SelectedLink = "https://example.invalid/guide";
        editor.SelectedImageSource = "C:\\pictures\\eri.png";

        var node = LayoutEditor.Find(editor.Document, editor.SelectedItem!.Id)!;
        Assert.Equal("circle", node.Properties["shape"]);
        Assert.Equal("https://example.invalid/guide", node.Properties["link"]);
        Assert.Equal("C:\\pictures\\eri.png", node.Properties["src"]);

        // Reading them back is what the property panel does when the selection comes back.
        Assert.Equal("circle", editor.SelectedShapeChoice!.Value);
        Assert.Equal("https://example.invalid/guide", editor.SelectedLink);
        Assert.Equal("C:\\pictures\\eri.png", editor.SelectedImageSource);

        // And they are part of the document, not of the view: a save/load keeps them.
        var saved = LayoutSerializer.Serialize(editor.Document);
        var reloaded = LayoutSerializer.Deserialize(saved);
        var kept = LayoutEditor.Find(reloaded, editor.SelectedItem!.Id)!;
        Assert.Equal("circle", kept.Properties["shape"]);
        Assert.Equal("https://example.invalid/guide", kept.Properties["link"]);
        Assert.Equal("C:\\pictures\\eri.png", kept.Properties["src"]);

        _ = host;
    }

    [Fact]
    public async Task Clearing_a_property_removes_it_rather_than_storing_a_blank()
    {
        var (editor, _) = await CreateEditorAsync();
        SelectFirst(editor);

        editor.SelectedLink = "https://example.invalid/one";
        editor.SelectedLink = string.Empty;

        var node = LayoutEditor.Find(editor.Document, editor.SelectedItem!.Id)!;
        Assert.False(node.Properties.ContainsKey("link"));
    }

    [Fact]
    public async Task The_document_json_reaches_the_page_that_exports_and_imports_it()
    {
        var (editor, _) = await CreateEditorAsync();

        // The export/import buttons share this one property. It used to be bound to nothing at all in
        // the view, so 导出 wrote into the void and 导入 always read an empty string and failed.
        editor.ExportDocumentCommand.Execute(null);
        Assert.False(string.IsNullOrWhiteSpace(editor.DocumentText));

        var before = LayoutEditor.Flatten(editor.Document).Count();
        editor.DocumentText = LayoutSerializer.Serialize(LayoutDocument.Empty("Home"));
        editor.ImportDocumentCommand.Execute(null);

        Assert.Empty(LayoutEditor.Flatten(editor.Document));
        Assert.NotEqual(before, LayoutEditor.Flatten(editor.Document).Count());
        Assert.Contains("导入", editor.Status, StringComparison.Ordinal);
    }
}
