using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using EriReborn.Core.Logging;
using EriReborn.Extension;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The load context an extension gets, and what it does and does not isolate.
///
/// <para>
/// Found by the same audit as the msiexec table: the gap document claimed "extension
/// default load context" as done, and nothing named the type. What it actually buys is
/// narrower than the comment on it says, which is the useful part to pin down.
/// </para>
/// </summary>
public sealed class ExtensionLoadContextTests
{
    /// <summary>A real assembly that is always next to the test assembly.</summary>
    private static string AnyRealAssembly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "EriReborn.Core.dll");
        Assert.True(File.Exists(path), "测试输出目录里应该有 EriReborn.Core.dll。");
        return path;
    }

    [Fact]
    public void The_context_is_collectible_so_an_extension_can_actually_be_unloaded()
    {
        // Without this flag "unload" means "stop using it", and the files stay locked —
        // which is what makes an extension unupdateable until the app restarts.
        var context = new ExtensionLoadContext("test-collectible", AnyRealAssembly());

        Assert.True(context.IsCollectible);
    }

    [Fact]
    public void Two_contexts_load_the_same_assembly_as_two_separate_assemblies()
    {
        // This is the isolation that is real: an extension's copy of a type is its own,
        // so nothing it does to that type can be seen by the host or by another
        // extension.
        var path = AnyRealAssembly();

        var first = new ExtensionLoadContext("test-first", path);
        var second = new ExtensionLoadContext("test-second", path);

        var a = first.LoadFromAssemblyPath(path);
        var b = second.LoadFromAssemblyPath(path);

        Assert.NotSame(a, b);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
        Assert.Same(a, first.LoadFromAssemblyPath(path));
    }

    private static object? ResolveLocally(ExtensionLoadContext context, string assemblyName)
    {
        var load = typeof(ExtensionLoadContext).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(load);
        return load!.Invoke(context, new object[] { new AssemblyName(assemblyName) });
    }

    [Fact]
    public void With_no_dependency_file_nothing_is_resolved_locally()
    {
        // AssemblyDependencyResolver reads a .deps.json beside the assembly. A
        // hand-authored extension often ships none, and the intended answer is to
        // resolve nothing and let the default context supply everything.
        //
        // Copied somewhere with no companion file, so the test says what it means rather
        // than depending on whatever the test output directory happens to contain.
        var alone = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(alone);
        var copy = Path.Combine(alone, "EriReborn.Core.dll");
        File.Copy(AnyRealAssembly(), copy);

        Assert.False(File.Exists(Path.ChangeExtension(copy, ".deps.json")), "这个目录里不该有 deps.json。");

        var context = new ExtensionLoadContext("test-no-deps", copy);

        foreach (var name in new[] { "System.Text.Json", "System.Runtime", "Newtonsoft.Json" })
        {
            Assert.Null(ResolveLocally(context, name));
        }
    }

    [Fact]
    public void A_library_the_dependency_file_lists_is_resolved_locally()
    {
        // The honest correction to what this class used to claim. Handed an assembly with
        // a companion .deps.json, the resolver does return paths for the libraries that
        // file lists — here a third-party one, observed rather than assumed.
        //
        // So the isolation is not "an extension cannot bring its own copy of a library".
        // It is that the copy lives in that extension's context: the host's own types are
        // untouched, and two extensions cannot see each other's.
        var context = new ExtensionLoadContext("test-with-deps", AnyRealAssembly());

        Assert.NotNull(ResolveLocally(context, "Newtonsoft.Json"));
    }

    [Fact]
    public void A_framework_assembly_still_comes_from_the_running_runtime()
    {
        // The other half of the same property: with nothing resolved locally, a type
        // from a framework assembly is the one the host already has, not a second copy.
        var context = new ExtensionLoadContext("test-framework", AnyRealAssembly());

        var json = context.LoadFromAssemblyName(new AssemblyName("System.Text.Json"));

        Assert.Same(AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("System.Text.Json")), json);
    }

    // --------------------------------------------------------- the loader

    private static IsolatedExtensionLoader Loader() => new(AppLog.For("Test"));

    private static ExtensionManifest Manifest(string id, string? assembly, string directory = "/nonexistent")
        => new()
        {
            Id = id,
            Name = id,
            Assembly = assembly,
            Directory = directory,
        };

    [Fact]
    public void An_extension_that_names_no_assembly_is_rejected()
    {
        var status = Loader().Load(Manifest("demo", null), null!);

        Assert.Equal(ExtensionLoadState.Rejected, status.State);
        Assert.Contains("assembly", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_assembly_name_is_rejected_the_same_way(string? assembly)
    {
        Assert.Equal(ExtensionLoadState.Rejected, Loader().Load(Manifest("demo", assembly), null!).State);
    }

    [Fact]
    public void An_extension_whose_assembly_is_missing_says_where_it_looked()
    {
        // "The extension did not load" is not a diagnosis; the path is.
        var missing = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "gone.dll");
        var status = Loader().Load(Manifest("demo", "gone.dll", Path.GetDirectoryName(missing)!), null!);

        Assert.Equal(ExtensionLoadState.AssemblyMissing, status.State);
        Assert.Contains("gone.dll", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unloading_something_that_was_never_loaded_is_false_not_an_error()
    {
        var loader = Loader();

        Assert.False(loader.Unload("never-loaded"));

        // And unloading everything on an empty loader is not an error either.
        loader.UnloadAll();
    }

    [Fact]
    public void The_loader_implements_the_lifecycle_the_registry_calls()
    {
        // The registry holds an IExtensionLifecycle; if this stopped implementing it the
        // disconnect would be a compile error, but the same is not true of the reverse —
        // so the relationship is worth stating.
        Assert.IsAssignableFrom<IExtensionLifecycle>(Loader());
    }

    [Fact]
    public void A_relative_assembly_path_is_resolved_against_the_manifest_directory()
    {
        // Not a load: this only proves the loader looks beside the manifest rather than
        // in the working directory, which is what lets the same extension load whatever
        // the process happened to start in.
        var directory = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var status = Loader().Load(Manifest("demo", "sub/demo.dll", directory), null!);

        Assert.Equal(ExtensionLoadState.AssemblyMissing, status.State);

        var expected = Path.GetFullPath(Path.Combine(directory, "sub", "demo.dll"));
        Assert.Contains(expected, status.Message, StringComparison.Ordinal);
    }
}
