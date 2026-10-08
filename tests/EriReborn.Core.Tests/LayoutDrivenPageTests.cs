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
/// The Overview page renders from its layout document. Before this, the schema
/// was only ever previewed inside the Creative Workshop, so editing it changed
/// nothing that actually ran.
/// </summary>
public sealed class LayoutDrivenPageTests
{
    private static async Task<(AppHost Host, string UserData)> CreateHostAsync()
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

        return (host, userData);
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

    [Fact]
    public async Task Every_binding_in_the_default_document_resolves()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var home = new HomeViewModel(host);
            var bindings = home.CreateBindings();
            var unresolved = new List<string>();

            foreach (var node in LayoutEditor.Flatten(home.Layout))
            {
                var parsed = LayoutBindings.Parse(node.Binding);
                if (parsed is null)
                {
                    continue;
                }

                var (kind, name) = parsed.Value;
                var resolved = kind switch
                {
                    "text" => bindings.Text(name) is not null,
                    "action" => bindings.Actions.ContainsKey(name),
                    "items" => bindings.Items?.Invoke(name) is { Count: > 0 },
                    "value" => bindings.Value?.Invoke(name) is not null,
                    _ => false,
                };

                if (!resolved)
                {
                    unresolved.Add($"{node.Id} -> {node.Binding}");
                }
            }

            // A typo here is invisible at runtime: the node silently keeps its
            // authored text, so the page looks plausible but shows no data.
            Assert.Empty(unresolved);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_default_document_carries_real_bindings_not_frozen_values()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var home = new HomeViewModel(host);
            var bindings = home.CreateBindings();

            var machine = LayoutEditor.Flatten(home.Layout).Single(n => n.Binding == "text:MachineName");
            Assert.Equal(home.MachineName, bindings.Text("MachineName"));

            // The value comes from the machine, not from the document.
            Assert.NotEqual(home.MachineName, machine.Text);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task The_default_document_passes_validation()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var home = new HomeViewModel(host);
            var result = LayoutValidator.Validate(home.Layout);

            Assert.True(result.IsValid, string.Join(" | ", result.Issues));
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Saving_a_layout_in_the_editor_is_what_the_home_page_renders()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var home = new HomeViewModel(host);
            var editor = new LayoutEditorViewModel(host);

            editor.AddNodeCommand.Execute(LayoutNodeType.Text);
            var added = editor.CanvasItems.Last().Id;

            Assert.Null(LayoutEditor.Find(home.Layout, added));

            editor.Select(editor.CanvasItems.Last());
            editor.SelectedText = "addedByEditor";
            editor.SaveDocumentCommand.Execute(null);
            Assert.Contains("已保存", editor.Status);

            // This is the loop that makes the schema real rather than decorative.
            home.ReloadLayout();

            var restored = LayoutEditor.Find(home.Layout, added);
            Assert.NotNull(restored);
            Assert.Equal("addedByEditor", restored!.Text);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Fact]
    public async Task Scan_progress_starts_at_zero_rather_than_a_made_up_number()
    {
        var (host, data) = await CreateHostAsync();
        try
        {
            var home = new HomeViewModel(host);

            Assert.Equal(0, home.ScanPercent);
        }
        finally
        {
            Cleanup(data);
        }
    }

    [Theory]
    [InlineData("text:MachineName", "text", "MachineName")]
    [InlineData("action:scan", "action", "scan")]
    [InlineData("items:CategoryBreakdown", "items", "CategoryBreakdown")]
    [InlineData("value:ScanPercent", "value", "ScanPercent")]
    public void Binding_names_are_split_into_kind_and_name(string binding, string kind, string name)
    {
        var parsed = LayoutBindings.Parse(binding);

        Assert.NotNull(parsed);
        Assert.Equal(kind, parsed!.Value.Kind);
        Assert.Equal(name, parsed.Value.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noColon")]
    [InlineData(":nameless")]
    public void An_unusable_binding_is_ignored_rather_than_guessed(string? binding)
    {
        Assert.Null(LayoutBindings.Parse(binding));
    }
}
