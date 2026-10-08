using EriReborn.Skin;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// Resolves a page action's key ("install", "refresh") to the art the active skin ships for
/// it. Only the UI layer may know about skins, so the key travels from the page and the
/// picture is chosen here; a family with no art for that action yields null, and the caller
/// falls back to its label rather than an empty plate.
/// </summary>
public static class ActionArt
{
    public static string? Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var skinId = App.Main?.ActiveSkin?.Id;
        if (string.IsNullOrWhiteSpace(skinId))
        {
            return null;
        }

        // "eri_windows" and "eri_android" are the same art family.
        var family = skinId.Split('_')[0];
        var id = "v2_" + family + "_btn_" + key;
        return SkinArtwork.Resolve(id) is null ? null : id;
    }
}
