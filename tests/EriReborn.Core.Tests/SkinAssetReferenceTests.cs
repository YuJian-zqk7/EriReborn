using System.Text.Json;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Art a skin names has to exist.
///
/// <para>
/// This is not tidiness. The android_style skin declares a character the manifest does not carry, the
/// picture resolved to nothing, and Avalonia then threw inside its own render pass while drawing the
/// control — which killed the process from the dispatcher, with the user's unsaved work gone. A dangling
/// reference is therefore a crash, and it is checked here, at build time, where it can be fixed calmly.
/// </para>
/// </summary>
public sealed class SkinAssetReferenceTests
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

    [Fact]
    public void Artwork_handed_to_a_control_is_never_disposed_under_it()
    {
        // The crash was not "a picture is missing": it was a picture being disposed while the compositor
        // was drawing it. SpritePlayer gives its frames to an Image's Source, so disposing them left the
        // control holding a bitmap with no platform picture, and Avalonia's Image.Render threw inside the
        // render pass — which ends the process, not the control. This keeps that call out of the file.
        var player = Path.Combine(
            RepositoryRoot(), "src", "EriReborn.UI.Avalonia", "SpritePlayer.cs");

        var source = File.ReadAllText(player);

        Assert.DoesNotContain("frame.Dispose()", source, StringComparison.Ordinal);
        Assert.Contains("SkinArtwork.Transparent", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_asset_a_skin_names_is_registered_in_the_manifest()
    {
        var root = RepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(root, "assets", "asset_manifest.json"));
        var problems = new List<string>();

        foreach (var skinDirectory in Directory.GetDirectories(Path.Combine(root, "assets", "skins")).OrderBy(path => path, StringComparer.Ordinal))
        {
            var skinFile = Path.Combine(skinDirectory, "skin.json");
            if (!File.Exists(skinFile))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(skinFile));
            if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var skin = Path.GetFileName(skinDirectory);

            foreach (var entry in assets.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = entry.Value.GetString();
                if (string.IsNullOrWhiteSpace(id) || id.Contains('*'))
                {
                    // A wildcard names a family of sheets rather than one picture.
                    continue;
                }

                // The manifest is the authority the running app asks, and it answers by id. An id that
                // appears nowhere in it is an id that resolves to nothing.
                if (!manifest.Contains($"\"{id}\"", StringComparison.Ordinal))
                {
                    problems.Add($"{skin}.{entry.Name} = {id}");
                }
            }
        }

        // Strict on purpose: a picture a skin names but does not have is what made switching to that skin
        // close the application. The theme skins used to name a character none of them shipped; they now
        // point at the Eri character the manifest really carries.
        Assert.Empty(problems);
    }
}
