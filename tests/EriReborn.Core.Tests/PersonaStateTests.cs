using EriReborn.Core.Logging;
using EriReborn.Persona;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// A persona decides how a fact is said, never what it is. The fact travels in
/// its own field, so there is nowhere for a voice to overwrite it (spec 4/103).
/// </summary>
public sealed class PersonaStateTests
{
    private static string AssetsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "personas");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到 assets/personas 目录。");
    }

    private static PersonaCatalog CatalogOfShippedPacks()
    {
        var catalog = new PersonaCatalog(AppLog.For("Test"));
        catalog.Load(PersonaCatalog.Discover(AssetsDirectory()));
        return catalog;
    }

    // ------------------------------------------------------------- coverage

    [Fact]
    public void Every_shipped_persona_speaks_for_every_state()
    {
        var missing = CatalogOfShippedPacks().MissingStateLines();

        // A state with no line would fall back to flat wording, which is exactly
        // where a persona stops being a persona.
        Assert.Empty(missing.Select(item => $"{item.PackId}:{item.State}"));
    }

    [Fact]
    public void The_shipped_packs_are_actually_loaded()
    {
        var catalog = CatalogOfShippedPacks();

        Assert.Equal(4, catalog.Count);
        Assert.Contains(catalog.Packs, pack => pack.Id == "Eri");
        Assert.Contains(catalog.Packs, pack => pack.Id == "Tech");
        Assert.Contains(catalog.Packs, pack => pack.Id == "Win11");
        Assert.Contains(catalog.Packs, pack => pack.Id == "Android");
    }

    [Fact]
    public void Every_state_maps_to_its_own_key()
    {
        var keys = PersonaStates.All.Select(PersonaStates.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // --------------------------------------------------- the fact is the fact

    [Fact]
    public void The_technical_text_survives_the_persona_untouched()
    {
        var catalog = CatalogOfShippedPacks();
        const string fact = "ERROR: Network Unreachable（host: pan.example.invalid）";

        var message = catalog.Say(PersonaState.NetworkError, fact);

        Assert.Equal(fact, message.Technical);
        Assert.Contains(fact, message.Display);
    }

    [Fact]
    public void The_fact_is_shown_before_the_voice()
    {
        var catalog = CatalogOfShippedPacks();

        // Without an active persona there is no voice at all, and the display is
        // just the fact — correct, but not what this test is about.
        catalog.Activate("Eri");

        const string fact = "安装失败：退出码 1603";

        var message = catalog.Say(PersonaState.InstallFailed, fact);

        // The voice comes after, so a long or cheerful line can never bury the fact.
        Assert.StartsWith(fact, message.Display, StringComparison.Ordinal);
        Assert.True(message.Display.Length > fact.Length);
    }

    [Fact]
    public void The_voice_is_carried_separately_from_the_fact()
    {
        var catalog = CatalogOfShippedPacks();

        var message = catalog.Say(PersonaState.InstallFailed, "退出码 1603");

        Assert.NotEqual(message.Technical, message.Voice);
        Assert.DoesNotContain("1603", message.Voice);
    }

    [Fact]
    public void A_persona_with_no_line_does_not_invent_one()
    {
        var catalog = new PersonaCatalog(AppLog.For("Test"));
        catalog.Load(new[] { new PersonaPack { Id = "silent" } });

        var message = catalog.Say(PersonaState.Success, "操作成功");

        // No charm where there is nothing to say, and the fact is unaffected.
        Assert.Equal("操作成功", message.Technical);
        Assert.Equal(string.Empty, message.Voice);
        Assert.Equal("操作成功", message.Display);
    }

    // ------------------------------------------------ unknown is not missing

    [Fact]
    public void Unknown_and_unsupported_are_their_own_states()
    {
        var catalog = CatalogOfShippedPacks();

        var unknown = catalog.Say(PersonaState.Unknown, "检测：Unknown");
        var unsupported = catalog.Say(PersonaState.Unsupported, "检测：Unsupported");

        Assert.NotEqual(unknown.State, unsupported.State);
        Assert.NotEqual(unknown.Voice, unsupported.Voice);

        // Neither is allowed to sound like a verdict of "missing".
        foreach (var message in new[] { unknown, unsupported })
        {
            Assert.DoesNotContain("没装", message.Voice);
            Assert.DoesNotContain("未安装", message.Voice);
            Assert.DoesNotContain("缺失", message.Voice);
        }
    }

    [Fact]
    public void Unknown_keeps_its_wording_even_when_the_voice_would_rather_be_cute()
    {
        var catalog = CatalogOfShippedPacks();

        foreach (var pack in catalog.Packs)
        {
            var line = pack.Messages[PersonaStates.Key(PersonaState.Unknown)];

            // The sentiment may be playful; the meaning may not drift to "missing".
            Assert.DoesNotContain("没装", line);
            Assert.DoesNotContain("未安装", line);
        }
    }

    // ------------------------------------------------------------- emotions

    [Theory]
    [InlineData(PersonaState.Success, PersonaEmotion.Proud)]
    [InlineData(PersonaState.InstallCompleted, PersonaEmotion.Proud)]
    [InlineData(PersonaState.InstallFailed, PersonaEmotion.Sad)]
    [InlineData(PersonaState.Warning, PersonaEmotion.Annoyed)]
    [InlineData(PersonaState.Waiting, PersonaEmotion.Sleepy)]
    [InlineData(PersonaState.PermissionRequired, PersonaEmotion.Worried)]
    [InlineData(PersonaState.Unknown, PersonaEmotion.Neutral)]
    [InlineData(PersonaState.Unsupported, PersonaEmotion.Neutral)]
    public void A_state_carries_an_emotion_for_the_character_to_show(PersonaState state, PersonaEmotion expected)
    {
        Assert.Equal(expected, PersonaStates.EmotionFor(state));
    }

    [Fact]
    public void Every_state_has_a_defined_emotion()
    {
        foreach (var state in PersonaStates.All)
        {
            Assert.True(Enum.IsDefined(PersonaStates.EmotionFor(state)));
        }
    }

    [Fact]
    public void Switching_persona_changes_the_voice_but_not_the_fact()
    {
        var catalog = CatalogOfShippedPacks();
        const string fact = "下载失败：HTTP 403";

        var voices = new List<string>();
        foreach (var pack in catalog.Packs.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            catalog.Activate(pack.Id);
            var message = catalog.Say(PersonaState.DownloadFailed, fact);

            Assert.Equal(fact, message.Technical);
            voices.Add(message.Voice);
        }

        // Four personas, four ways of saying it — and one unchanged fact.
        Assert.Equal(voices.Count, voices.Distinct(StringComparer.Ordinal).Count());
    }
}
