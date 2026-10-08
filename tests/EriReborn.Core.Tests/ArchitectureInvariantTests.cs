using System.Text.RegularExpressions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// The layering and the "no fakes" rules, as tests rather than as prose.
///
/// <para>
/// A coverage audit found these claims in the gap document with no mechanical check
/// behind them: "Core does not depend on the platform", "five cloud platforms, all
/// peers", "no Noop pretending to succeed". Reading the code and agreeing with it is
/// not a check — this session has produced several bugs that survived exactly that.
/// These tests read the project files and the sources, so the next person who adds a
/// using, a priority field or a sixth provider finds out immediately.
/// </para>
/// </summary>
public sealed class ArchitectureInvariantTests
{
    /// <summary>A quote, so these patterns contain no backslash at all.</summary>
    private const char Q = '"';

    private static readonly string ProjectReferencePattern = "ProjectReference Include=" + Q + "([^" + Q + "]+)" + Q;

    // [A-Za-z0-9_] and not [A-Za-z]: the ids include "Pan123" and the classes include
    // "Provider123", and a pattern that cannot see digits quietly counts four where
    // there are five.
    private static readonly string ProviderIdPattern = "public const string [A-Za-z0-9_]+ = " + Q + "([^" + Q + "]+)" + Q;

    // The five providers do not get constructed inside the platform services any more: they
    // ship as official extension packages and reach the host through
    // IExtensionHost.RegisterCloudProvider. So "which providers does a platform get" is a
    // question about the packages that platform package references, not about `new` calls.
    private static readonly string CloudPackagePattern = "(EriReborn\\.Ext\\.Cloud\\.[A-Za-z0-9]+)";

    private static string Root()
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

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    private static string ProjectFile(string project)
        => Source(Path.Combine("src", project, project + ".csproj"));

    /// <summary>The layers a project is allowed to reference, per the specification's layering.</summary>
    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.Ordinal)
    {
        // The bottom of the stack. Core knows about nothing above it.
        ["EriReborn.Core"] = Array.Empty<string>(),

        // The platform contract sits on Core and nothing else.
        ["EriReborn.Platform.Abstractions"] = new[] { "EriReborn.Core" },

        // Leaf modules: each is a feature area with Core beneath it.
        ["EriReborn.Skin"] = new[] { "EriReborn.Core" },
        ["EriReborn.Asset"] = new[] { "EriReborn.Core" },
        ["EriReborn.Layout"] = new[] { "EriReborn.Core" },
        ["EriReborn.Persona"] = new[] { "EriReborn.Core" },

        // Cloud talks to platforms, never to the software engine: providers must not be
        // hard-coded into one another.
        ["EriReborn.Cloud"] = new[] { "EriReborn.Core", "EriReborn.Platform.Abstractions" },

        ["EriReborn.Engine"] = new[] { "EriReborn.Core", "EriReborn.Platform.Abstractions", "EriReborn.Cloud" },

        // Extension is the host-contract layer: an extension host has to be able to hand a
        // cloud provider back to the app (RegisterCloudProvider takes an ICloudProvider) and
        // a download engine, so both Cloud and Engine sit beneath it. The signature cannot
        // live in Core instead — Core_source_never_names_a_layer_above_it forbids the word
        // "EriReborn.Cloud" in Core at all.
        ["EriReborn.Extension"] = new[] { "EriReborn.Core", "EriReborn.Platform.Abstractions", "EriReborn.Engine", "EriReborn.Cloud" },

        // The real platform implementations may use everything beneath them.
        ["EriReborn.Platform.Windows"] = new[] { "EriReborn.Core", "EriReborn.Platform.Abstractions", "EriReborn.Cloud", "EriReborn.Engine" },
        ["EriReborn.Platform.Android"] = new[] { "EriReborn.Core", "EriReborn.Platform.Abstractions", "EriReborn.Cloud", "EriReborn.Engine" },
    };

    private static IReadOnlyList<string> References(string project)
        => Regex.Matches(ProjectFile(project), ProjectReferencePattern)
            .Select(match => Path.GetFileNameWithoutExtension(match.Groups[1].Value))
            .ToList();

    [Fact]
    public void Every_project_references_only_what_its_layer_allows()
    {
        var problems = new List<string>();

        foreach (var (project, allowed) in AllowedReferences)
        {
            foreach (var reference in References(project))
            {
                if (!allowed.Contains(reference, StringComparer.Ordinal))
                {
                    problems.Add(project + " 引用了 " + reference);
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Core_references_nothing_at_all()
    {
        // The first line of the layering, and the one every other claim rests on.
        Assert.Empty(References("EriReborn.Core"));
    }

    [Fact]
    public void Core_source_never_names_a_layer_above_it()
    {
        // A project reference can be absent while the code still reaches upward through a
        // type that happens to live elsewhere; this is the second half of the check.
        var forbidden = new[]
        {
            "EriReborn.Platform", "EriReborn.Engine", "EriReborn.Cloud",
            "EriReborn.Extension", "EriReborn.Skin", "EriReborn.Asset",
        };

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "EriReborn.Core"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var name in forbidden)
            {
                if (text.Contains(name, StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(file) + " 提到了 " + name);
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_cloud_layer_does_not_know_about_the_software_engine()
    {
        Assert.DoesNotContain("EriReborn.Engine", References("EriReborn.Cloud"));
    }

    [Fact]
    public void Every_layer_directory_is_covered_by_this_test()
    {
        // A new project nobody added here would silently go unchecked.
        var skip = new[] { "EriReborn.App.Shared", "EriReborn.Desktop", "EriReborn.Mobile", "EriReborn.UI.Avalonia" };

        var declared = Directory
            .EnumerateDirectories(Path.Combine(Root(), "src"), "EriReborn.*")
            .Select(Path.GetFileName)
            .Where(name => name is not null && !skip.Contains(name, StringComparer.Ordinal))
            .ToList();

        Assert.NotEmpty(declared);

        foreach (var project in declared)
        {
            Assert.True(
                AllowedReferences.ContainsKey(project!),
                project + " 是新的层，但这里没有它的允许引用表。");
        }
    }

    [Fact]
    public void Nothing_in_the_source_pretends_to_be_unimplemented()
    {
        // "Do not fake success, and do not leave a Noop on the production path."
        var markers = new[] { "尚未实现", "NotImplementedException", "TODO", "FIXME", "HACK", "throw new NotSupportedException" };

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var marker in markers)
            {
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    offenders.Add(Path.GetFileName(file) + " 含有 " + marker);
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_five_cloud_platforms_are_declared_as_peers()
    {
        var declared = Regex.Matches(
                Source(Path.Combine("src", "EriReborn.Cloud", "CloudProviderIds.cs")),
                ProviderIdPattern)
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(5, declared.Count);
        Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_provider_contract_carries_no_priority()
    {
        // "No master and no servants" is a property of the interface, not a comment: a
        // priority field is how the ordering quietly comes back.
        var contract = Source(Path.Combine("src", "EriReborn.Cloud", "ICloudProvider.cs"));

        foreach (var word in new[] { "Priority", "Rank", "Order", "Default", "Preferred" })
        {
            Assert.DoesNotContain(word, contract, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_user_facing_text_marks_one_platform_as_the_only_one()
    {
        // "All five are peers" is a property of what we tell people, not only of the
        // registry. A prompt that says auto-download works on one platform is the exact
        // regression that shipped once, so the wording is pinned here rather than trusted.
        var platformNames = Directory
            .EnumerateFiles(Path.Combine(Root(), "src", "EriReborn.Cloud", "Providers"), "Provider*.cs")
            .SelectMany(file => Regex.Matches(
                File.ReadAllText(file),
                "DisplayName => " + Q + "([^" + Q + "]+)" + Q))
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(platformNames);

        var exclusivePhrases = new[] { "只支持", "仅支持", "只对", "目前只", "只有", "独家" };

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var named = platformNames.Where(name => lines[i].Contains(name, StringComparison.Ordinal)).ToList();
                if (named.Count == 0)
                {
                    continue;
                }

                foreach (var phrase in exclusivePhrases)
                {
                    if (lines[i].Contains(phrase, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1} 说 {string.Join("/", named)} {phrase}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Both_platforms_assemble_the_same_five_cloud_providers()
    {
        // Asking whether "five providers, all peers" was actually true is how this test came
        // to exist. It is true, and a missing provider should still fail a test rather than
        // needing someone to count lines. What changed is where to look: the providers are
        // contributed by official extension packages now, so this reads the packages each
        // platform package assembles.
        static IReadOnlyList<string> CloudPackages(string project)
            => Regex.Matches(Source(Path.Combine("src", project, project + ".csproj")), CloudPackagePattern)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

        var desktop = CloudPackages("EriReborn.Desktop");
        var mobile = CloudPackages("EriReborn.Mobile");

        Assert.Equal(
            new[]
            {
                "EriReborn.Ext.Cloud.Baidu",
                "EriReborn.Ext.Cloud.Lanzou",
                "EriReborn.Ext.Cloud.Pan123",
                "EriReborn.Ext.Cloud.Quark",
                "EriReborn.Ext.Cloud.Xunlei",
            },
            desktop);

        // The phone build must show the same five platforms, not a subset.
        Assert.Equal(desktop, mobile);
    }

    [Fact]
    public void A_provider_cannot_claim_more_than_it_does()
    {
        // The three kinds are the whole vocabulary for "how it really talks to the
        // platform"; anything else would be a way to overstate.
        var kinds = Enum.GetNames<EriReborn.Cloud.CloudImplementationKind>();

        Assert.Equal(3, kinds.Length);
        Assert.Contains("OfficialApi", kinds);
        Assert.Contains("HtmlParsing", kinds);
        Assert.Contains("NotImplemented", kinds);
    }
}
