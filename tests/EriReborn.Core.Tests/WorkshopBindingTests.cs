using System.Text.RegularExpressions;
using EriReborn.App.Shared.ViewModels;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The workshop's XAML against the page that feeds it.
///
/// <para>
/// This exists because of a real defect: the layout editor's 「导出 JSON」 and 「从 JSON 导入」 buttons
/// called commands that wrote and read <c>LayoutEditorViewModel.DocumentText</c>, and no control in the
/// view was ever bound to that property. Both buttons were therefore dead — export wrote into the void,
/// import always read an empty string and failed — and every test passed, because the tests set the
/// property directly. Nothing but a check of the view against the page can catch a binding that reaches
/// nothing.
/// </para>
/// </summary>
public sealed class WorkshopBindingTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "assets", "skins")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private static string WorkshopXaml()
        => File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "EriReborn.UI.Avalonia", "Views", "WorkshopView.axaml"));

    /// <summary>
    /// Every property reachable from the page: the page's own, plus one level down, because the view
    /// binds into <c>LayoutEditor.</c>, <c>PluginBuilder.</c> and the extension page the same way.
    /// </summary>
    private static HashSet<string> ReachableProperties()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var page = typeof(WorkshopViewModel);

        foreach (var property in page.GetProperties())
        {
            names.Add(property.Name);

            if (property.PropertyType.IsClass
                && property.PropertyType.Namespace?.StartsWith("EriReborn", StringComparison.Ordinal) == true)
            {
                foreach (var nested in property.PropertyType.GetProperties())
                {
                    names.Add(nested.Name);
                }
            }
        }

        return names;
    }

    [Fact]
    public void Every_command_the_workshop_binds_through_the_page_exists()
    {
        var xaml = WorkshopXaml();

        // Only the bindings that name the page (or one of its children) are checked. A bare
        // {Binding SomeCommand} inside an item template belongs to the row's own type — a plugin or
        // extension row has its own commands — and has nothing to do with this page.
        var patterns = new[]
        {
            @"\$parent\[UserControl\]\.\(\(vm:WorkshopViewModel\)DataContext\)\.([A-Za-z]+Command)",
            @"\{Binding [A-Za-z]+\.([A-Za-z][A-Za-z0-9]*Command)\}",
        };

        var bound = patterns
            .SelectMany(pattern => Regex.Matches(xaml, pattern).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(bound);

        var available = ReachableProperties();
        var missing = bound.Where(name => !available.Contains(name)).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void The_layout_document_text_is_bound_so_export_and_import_are_not_dead_buttons()
    {
        // The one property behind both buttons. A command alone is not a feature: if nothing shows the
        // text and nothing lets the user paste it, the two buttons cannot work.
        Assert.Contains("{Binding LayoutEditor.DocumentText}", WorkshopXaml(), StringComparison.Ordinal);

        var names = ReachableProperties();
        foreach (var name in new[] { "ExportDocumentCommand", "ImportDocumentCommand", "DocumentText" })
        {
            Assert.Contains(name, names);
        }
    }

    [Fact]
    public void The_editor_offers_the_mouse_tool_and_the_page_the_tree_and_the_window()
    {
        var xaml = WorkshopXaml();

        // 选择（鼠标）as a tool, the page tree that shows a parent's children, and the way out to a
        // window — the three things the editor was missing.
        Assert.Contains("CommandParameter=\"选择\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PreviewPageTree\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnOpenEditorWindowClick", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowSelectionPanel", xaml, StringComparison.Ordinal);

        // The flat picker it replaced, which could not show children at all.
        Assert.DoesNotContain("PreviewPagePicker", xaml, StringComparison.Ordinal);

        var names = ReachableProperties();
        Assert.Contains("ShowSelectionPanel", names);
    }

    [Fact]
    public void Layout_is_a_tool_inside_the_editor_and_no_longer_a_page_of_its_own()
    {
        var xaml = WorkshopXaml();

        // 布局 used to be a second tab of the workshop, so the skin editor's 布局 button sent the user out
        // of the editor and into another page. It is one of the editor's own states now: the page swaps
        // its middle for the canvas while the tool is in hand.
        Assert.Contains("ShowLayoutTool", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"布局\"", xaml, StringComparison.Ordinal);

        Assert.DoesNotContain("LayoutEditorTab", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnOpenLayoutEditorClick", xaml, StringComparison.Ordinal);

        // The layout canvas and its live preview still have to be reachable by name: the editor's drag
        // handlers and the preview refresh look them up.
        Assert.Contains("Name=\"PreviewHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LayoutEditor.CanvasItems", xaml, StringComparison.Ordinal);

        Assert.Contains("ShowLayoutTool", ReachableProperties());
    }

    [Fact]
    public void The_editor_window_is_a_real_window_over_the_same_page()
    {
        var window = Path.Combine(
            RepositoryRoot(), "src", "EriReborn.UI.Avalonia", "Views", "SkinEditorWindow.cs");

        Assert.True(File.Exists(window), $"缺少独立编辑器窗口：{window}");

        var source = File.ReadAllText(window);

        // It has to host the very same editor view, so the window is room rather than a second editor
        // with its own state, and it must be a Window (top-level) rather than another page.
        Assert.Contains(": Window", source, StringComparison.Ordinal);
        Assert.Contains("new WorkshopView()", source, StringComparison.Ordinal);
    }
}
