using EriReborn.Core.Logging;
using EriReborn.Persona;
using EriReborn.Skin;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Persona is a layer separate from Skin (spec 57). These tests use the shipped
/// packs and skins, so a skin that names a missing voice, or a pack that drops a
/// key the UI asks for, fails here.
/// </summary>
public sealed class PersonaCatalogTests
{
    private static string AssetsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "asset_manifest.json");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "assets");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("assets/ was not found above the test directory.");
    }

    private static IReadOnlyList<PersonaPack> Packs() => PersonaCatalog.Discover(Path.Combine(AssetsRoot(), "personas"));

    private static async Task<IReadOnlyList<SkinManifest>> SkinsAsync()
    {
        var engine = new SkinEngine(AppLog.For("Test"));
        return await engine.DiscoverAsync(Path.Combine(AssetsRoot(), "skins"));
    }

    private static PersonaCatalog Loaded()
    {
        var catalog = new PersonaCatalog(AppLog.For("Test"));
        catalog.Load(Packs());
        return catalog;
    }

    [Fact]
    public void The_shipped_packs_are_discovered()
    {
        var catalog = Loaded();

        Assert.Equal(4, catalog.Count);
        Assert.NotNull(catalog.Find("Eri"));
        Assert.NotNull(catalog.Find("Tech"));
        Assert.NotNull(catalog.Find("Win11"));
        Assert.NotNull(catalog.Find("Android"));
    }

    [Fact]
    public void Discovery_is_case_insensitive()
    {
        Assert.NotNull(Loaded().Find("eri"));
        Assert.NotNull(Loaded().Find("TECH"));
    }

    [Fact]
    public void Every_pack_supplies_every_key_the_ui_asks_for()
    {
        var keys = new[]
        {
            PersonaKeys.OverviewGreeting,
            PersonaKeys.ScanIdle,
            PersonaKeys.ScanPreparing,
            PersonaKeys.ScanDone,
            PersonaKeys.ScanFailed,
            PersonaKeys.CatalogHealthy,
            PersonaKeys.CatalogRejected,
            PersonaKeys.MemoryUnknown,
        };

        foreach (var pack in Packs())
        {
            foreach (var key in keys)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(pack.Messages.GetValueOrDefault(key)),
                    $"Persona '{pack.Id}' is missing '{key}'.");
            }
        }
    }

    [Fact]
    public void The_packs_are_genuinely_different_voices()
    {
        var greetings = Packs().Select(p => p.Messages[PersonaKeys.OverviewGreeting]).ToList();

        Assert.Equal(greetings.Count, greetings.Distinct(StringComparer.Ordinal).Count());

        // Not merely different strings — different lengths, i.e. different registers.
        Assert.True(Packs().Select(p => p.Messages[PersonaKeys.ScanIdle].Length).Distinct().Count() >= 3);
    }

    [Fact]
    public async Task Every_skin_declares_a_persona_that_actually_exists()
    {
        var catalog = Loaded();

        foreach (var skin in await SkinsAsync())
        {
            Assert.False(string.IsNullOrWhiteSpace(skin.Persona), $"Skin '{skin.Id}' declares no persona.");
            Assert.True(
                catalog.Find(skin.Persona!) is not null,
                $"Skin '{skin.Id}' names persona '{skin.Persona}' but no pack provides it.");
        }
    }

    [Fact]
    public void An_unknown_or_absent_persona_falls_back_to_neutral()
    {
        var catalog = Loaded();

        Assert.Equal("neutral", catalog.Activate("does-not-exist").Id);
        Assert.Equal("neutral", catalog.Activate(null).Id);
        Assert.Equal("neutral", catalog.Activate("   ").Id);

        // Wording still resolves, from the caller's own fallback.
        Assert.Equal("fallback text", catalog.Say("no.such.key", "fallback text"));
    }

    [Fact]
    public void Activation_switches_the_voice_and_raises_changed_once_per_switch()
    {
        var catalog = Loaded();
        var raised = 0;
        catalog.Changed += (_, _) => raised++;

        catalog.Activate("Eri");
        Assert.Equal("Eri", catalog.ActiveOrDefault.Id);
        Assert.Equal(1, raised);

        // Selecting the same pack again must not churn the UI.
        catalog.Activate("Eri");
        Assert.Equal(1, raised);

        catalog.Activate("Tech");
        Assert.Equal("Tech", catalog.ActiveOrDefault.Id);
        Assert.Equal(2, raised);

        var eri = catalog.Find("Eri")!;
        Assert.NotEqual(eri.Say(PersonaKeys.ScanDone, ""), catalog.Say(PersonaKeys.ScanDone, ""));
    }

    [Fact]
    public void A_missing_key_uses_the_callers_fallback_not_an_empty_string()
    {
        var pack = new PersonaPack { Id = "sparse", Name = "Sparse" };

        Assert.Equal("原文", pack.Say("anything", "原文"));
        Assert.Equal("原文", pack.Say("", "原文"));
    }

    [Fact]
    public void A_malformed_pack_is_skipped_rather_than_failing_the_catalog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "erireborn-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "good.json"), "{ \"id\": \"Good\", \"name\": \"Good\" }");
            File.WriteAllText(Path.Combine(directory, "broken.json"), "{ this is not json");
            File.WriteAllText(Path.Combine(directory, "nameless.json"), "{ \"name\": \"No Id\" }");

            var packs = PersonaCatalog.Discover(directory);

            Assert.Single(packs);
            Assert.Equal("Good", packs[0].Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Discovery_of_a_missing_directory_returns_empty()
    {
        Assert.Empty(PersonaCatalog.Discover(Path.Combine(Path.GetTempPath(), "erireborn-tests", "nope-" + Guid.NewGuid().ToString("N"))));
    }
}
