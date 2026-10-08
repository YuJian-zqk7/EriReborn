using EriReborn.Core.Domain;
using EriReborn.Engine.Software;
using EriReborn.Platform.Abstractions;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Suggestions turn "unknown" into "detectable" using real installed evidence.
/// A loose matcher would be worse than none, so the thresholds are asserted.
/// </summary>
public sealed class DetectionSuggesterTests
{
    private static SoftwareDefinition Software(string id, string name) => new()
    {
        Id = id,
        Name = name,
        CategoryId = "Utility",
        DirectoryName = id,
        Trust = SoftwareTrust.Verified,
        Detector = DetectorSpec.None,
        Sources = new[] { new SoftwareSource { Kind = SourceKind.HttpUrl, Url = "https://example.invalid/a.exe" } },
    };

    private static InstalledSoftwareInfo Installed(string name, string? version = null) => new(name, version);

    [Fact]
    public void An_identical_name_matches_exactly()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("bandizip", "Bandizip") },
            new[] { Installed("bandizip") });

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(SuggestionConfidence.Exact, suggestion.Confidence);
        Assert.Equal("bandizip", suggestion.SoftwareId);
    }

    [Fact]
    public void Punctuation_and_spacing_do_not_prevent_a_match()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("vsc", "Visual Studio Code") },
            new[] { Installed("VisualStudioCode") });

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(SuggestionConfidence.Normalized, suggestion.Confidence);
    }

    [Fact]
    public void A_longer_installed_name_is_matched_by_containment()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("java8", "Java 8") },
            new[] { Installed("Java 8 Update 481 (64-bit)") });

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(SuggestionConfidence.Containment, suggestion.Confidence);
    }

    [Fact]
    public void Short_names_are_not_matched_by_containment()
    {
        // "QQ" is far too short to be evidence that "QQBrowser" is the same product.
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("qq", "QQ") },
            new[] { Installed("QQBrowser") });

        Assert.Empty(suggestions);
    }

    [Fact]
    public void A_version_suffix_produces_a_prefix_pattern_that_survives_updates()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("java8", "Java 8") },
            new[] { Installed("Java 8 Update 481 (64-bit)") });

        var suggestion = Assert.Single(suggestions);

        // Assert behaviour, not the exact escaping: the pattern must keep
        // matching after the vendor bumps the version number.
        Assert.Matches(suggestion.ArpPattern, "Java 8 Update 481 (64-bit)");
        Assert.Matches(suggestion.ArpPattern, "Java 8 Update 999 (64-bit)");
        Assert.DoesNotMatch(suggestion.ArpPattern, "Java 11 Runtime");
        Assert.Contains("容忍版本号变化", suggestion.Reason);
    }

    [Fact]
    public void A_non_prefix_match_is_anchored_on_both_ends()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("vsc", "Visual Studio Code") },
            new[] { Installed("VisualStudioCode") });

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("^VisualStudioCode$", suggestion.ArpPattern);
    }

    [Fact]
    public void An_exact_name_that_is_not_a_prefix_is_anchored()
    {
        // "Code" does not start "Visual Studio Code", so no prefix pattern.
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("vsc", "Code") },
            new[] { Installed("Visual Studio Code") });

        var suggestion = Assert.Single(suggestions);
        Assert.Matches(suggestion.ArpPattern, "Visual Studio Code");
        Assert.DoesNotMatch(suggestion.ArpPattern, "Visual Studio Code Insiders");
    }

    [Fact]
    public void The_strongest_matching_installed_entry_wins()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("demo", "Demo Tool") },
            new[] { Installed("Demo Tool Suite"), Installed("Demo Tool") });

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(SuggestionConfidence.Exact, suggestion.Confidence);
        Assert.Equal("Demo Tool", suggestion.InstalledName);
    }

    [Fact]
    public void Nothing_matching_produces_no_suggestion()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("demo", "Completely Different") },
            new[] { Installed("Something Else Entirely") });

        Assert.Empty(suggestions);
    }

    [Fact]
    public void A_name_made_only_of_punctuation_is_ignored_rather_than_matching_everything()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("odd", "---") },
            new[] { Installed("Anything") });

        Assert.Empty(suggestions);
    }

    [Fact]
    public void Regex_metacharacters_in_names_are_escaped()
    {
        // The "." in "Node.js" must be literal: if it were not escaped the
        // pattern would also match an unrelated name.
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("nodejs", "Node.js") },
            new[] { Installed("Node.js") });

        var suggestion = Assert.Single(suggestions);
        Assert.Matches(suggestion.ArpPattern, "Node.js");

        // A prefix pattern deliberately tolerates suffixes, so the escaping test
        // must contrast a real "." with a name that only matches if "." is wild.
        Assert.DoesNotMatch(suggestion.ArpPattern, "NodeXjs");
        Assert.DoesNotMatch(suggestion.ArpPattern, "NodeXjs Extra");
    }

    [Fact]
    public void A_weaker_claim_on_an_already_claimed_program_is_dropped()
    {
        // Both entries are called "AIMP"-ish, but AIMP matches the installed
        // program exactly while AIMP2 only contains it. AIMP2 is a different
        // product and must not be pointed at AIMP.
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("aimp", "AIMP"), Software("aimp2", "AIMP2") },
            new[] { Installed("AIMP") });

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("aimp", suggestion.SoftwareId);
        Assert.Equal(SuggestionConfidence.Exact, suggestion.Confidence);
    }

    [Fact]
    public void Two_equally_good_claims_are_dropped_as_ambiguous()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("alpha_a", "Tools"), Software("alpha_b", "Tools") },
            new[] { Installed("Tools") });

        Assert.Empty(suggestions);
    }

    [Fact]
    public void Distinct_programs_are_all_kept()
    {
        var suggestions = DetectionSuggester.Suggest(
            new[] { Software("a", "Alpha"), Software("b", "Beta") },
            new[] { Installed("Alpha"), Installed("Beta") });

        Assert.Equal(2, suggestions.Count);
    }

    [Fact]
    public void Results_are_deterministic_across_input_orderings()
    {
        var candidates = new[] { Software("b", "Beta"), Software("a", "Alpha") };
        var installedA = new[] { Installed("Alpha"), Installed("Beta") };
        var installedB = new[] { Installed("Beta"), Installed("Alpha") };

        var first = DetectionSuggester.Suggest(candidates, installedA);
        var second = DetectionSuggester.Suggest(candidates, installedB);

        Assert.Equal(first.Select(s => s.SoftwareId), second.Select(s => s.SoftwareId));
        Assert.Equal(first.Select(s => s.ArpPattern), second.Select(s => s.ArpPattern));
    }

    [Fact]
    public void The_suggestion_converts_into_a_hint_that_only_sets_the_pattern()
    {
        var suggestion = Assert.Single(DetectionSuggester.Suggest(
            new[] { Software("demo", "Demo Tool") },
            new[] { Installed("Demo Tool") }));

        var hint = suggestion.ToHint();

        Assert.Matches(hint.ArpPattern!, "Demo Tool");
        Assert.Null(hint.Path);
        Assert.Null(hint.FileName);
        Assert.False(hint.IsEmpty);
    }

    [Fact]
    public void Normalisation_keeps_letters_and_digits_only()
    {
        Assert.Equal("visualstudiocode", DetectionSuggester.Normalize("Visual Studio Code"));
        Assert.Equal("7zip2409", DetectionSuggester.Normalize("7-Zip 24.09"));
        Assert.Equal("", DetectionSuggester.Normalize(" - "));
    }

    [Fact]
    public void Bulk_apply_writes_once_and_skips_empty_hints()
    {
        var path = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"), "hints.json");
        try
        {
            var store = new DetectionHintStore(path, EriReborn.Core.Logging.AppLog.For("Test"));
            var applied = store.SetMany(new Dictionary<string, DetectionHint>
            {
                ["a"] = new() { ArpPattern = "^Alpha" },
                ["b"] = new() { Path = @"C:\Tools\b.exe" },
                ["c"] = new(),
            });

            Assert.Equal(2, applied);
            Assert.Equal(2, store.Count);

            var reloaded = new DetectionHintStore(path, EriReborn.Core.Logging.AppLog.For("Test"));
            reloaded.Load();
            Assert.Equal("^Alpha", reloaded.Get("a")!.ArpPattern);
            Assert.Equal(@"C:\Tools\b.exe", reloaded.Get("b")!.Path);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
