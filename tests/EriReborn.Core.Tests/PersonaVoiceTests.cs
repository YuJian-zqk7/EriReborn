using EriReborn.App.Shared.Services;
using EriReborn.Core.Logging;
using EriReborn.Persona;
using Xunit;

namespace EriReborn.Core.Tests;

/// <summary>
/// Persona wording and skin wording are two systems (spec 57/7). A skin overrides a persona line by
/// storing it under a <c>persona.</c> prefix in its <c>texts</c> section, so the two key spaces stay
/// distinct and a UI-word edit cannot reach the character's voice — or the other way round.
/// </summary>
public sealed class PersonaVoiceTests
{
    private static PersonaCatalog CatalogWith(string id, params (string Key, string Line)[] lines)
    {
        var messages = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, line) in lines)
        {
            messages[key] = line;
        }

        var pack = new PersonaPack { Id = id, Name = id, Messages = messages };

        var catalog = new PersonaCatalog(AppLog.For("Test"));
        catalog.Load(new[] { pack });
        catalog.Activate(id);
        return catalog;
    }

    [Fact]
    public void A_skin_override_replaces_the_pack_line()
    {
        var catalog = CatalogWith("eri", ("overview.greeting", "出厂：概览就绪。"));

        // The user's skin rewords the greeting.
        var skin = new Dictionary<string, string> { ["persona.overview.greeting"] = "我的：搞定啦欧尼酱！" };

        var greeting = PersonaVoice.Voice(skin, catalog, PersonaKeys.OverviewGreeting, "环境概览已就绪。");

        Assert.Equal("我的：搞定啦欧尼酱！", greeting);
    }

    [Fact]
    public void Without_an_override_the_pack_line_stands()
    {
        var catalog = CatalogWith("eri", ("overview.greeting", "出厂：概览就绪。"));

        var greeting = PersonaVoice.Voice(
            new Dictionary<string, string>(),
            catalog,
            PersonaKeys.OverviewGreeting,
            "环境概览已就绪。");

        Assert.Equal("出厂：概览就绪。", greeting);
    }

    [Fact]
    public void Without_a_pack_line_the_fallback_stands()
    {
        var catalog = CatalogWith("eri"); // defines no greeting at all

        var greeting = PersonaVoice.Voice(
            new Dictionary<string, string>(),
            catalog,
            PersonaKeys.OverviewGreeting,
            "环境概览已就绪。");

        Assert.Equal("环境概览已就绪。", greeting);
    }

    [Fact]
    public void A_ui_text_override_does_not_bleed_into_the_persona()
    {
        // The whole point of the separate key space: editing "首页" must not change what the
        // character says, even though both ride on the same texts section.
        var catalog = CatalogWith("eri", ("overview.greeting", "出厂：概览就绪。"));
        var skin = new Dictionary<string, string> { ["nav.home"] = "我的首页" };

        var greeting = PersonaVoice.Voice(skin, catalog, PersonaKeys.OverviewGreeting, "环境概览已就绪。");

        Assert.Equal("出厂：概览就绪。", greeting);
    }

    [Fact]
    public void A_state_line_override_keeps_the_fact_and_the_face()
    {
        var catalog = CatalogWith("eri", ("state.error", "出厂：出错了。"));
        var skin = new Dictionary<string, string> { ["persona.state.error"] = "我的：坏掉了啦！" };

        var message = PersonaVoice.Message(skin, catalog, PersonaState.Error, "真实的失败原因");

        // Only the voice changed; the technical fact and the emotion the engine chose are untouched.
        Assert.Equal("我的：坏掉了啦！", message.Voice);
        Assert.Equal("真实的失败原因", message.Technical);
        Assert.Equal(PersonaEmotion.Sad, message.Emotion);
    }

    [Fact]
    public void A_state_line_without_an_override_uses_the_pack_voice()
    {
        var catalog = CatalogWith("eri", ("state.error", "出厂：出错了。"));

        var message = PersonaVoice.Message(
            new Dictionary<string, string>(),
            catalog,
            PersonaState.Error,
            "真实的失败原因");

        Assert.Equal("出厂：出错了。", message.Voice);
        Assert.Equal("真实的失败原因", message.Technical);
    }
}
