using Avalonia.Media.Imaging;
using EriReborn.Skin;

namespace EriReborn.UI.Avalonia;

/// <summary>
/// Resolves visual resources for the UI. Pages and windows ask for an asset id;
/// the file path is never hard-coded (spec 48), and a missing asset yields null
/// instead of an invented placeholder.
/// </summary>
public static class SkinArtwork
{
    /// <summary>Id of the brand mark every platform ships (manifest: platform=all).</summary>
    public const string BrandLogoId = "erireborn_launcher_icon";

    /// <summary>
    /// Keyed by file path *and* the file's timestamp and size.
    ///
    /// <para>
    /// Keying on the asset id alone was wrong in exactly the way a skin editor would notice:
    /// replacing a picture overwrites the file that id points at, so the cache would keep serving
    /// the old bitmap and "替换" would appear to do nothing until a restart.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A one-pixel transparent picture, for a control that has no art but is still in the tree.
    ///
    /// <para>
    /// An Image is allowed to have no Source, but a control the compositor has already committed a draw
    /// for can be asked to draw again in the very frame it loses its picture — and Avalonia's own
    /// Image.Render then threw, inside the render pass, where the exception takes the whole application
    /// down instead of one control. That is what a skin naming a picture it does not have did to the
    /// process. Handing out a transparent pixel removes the possibility; whether there is real art is
    /// still said by HasArt, so nothing else has to change.
    /// </para>
    /// </summary>
    /// <summary>A 1×1 fully transparent PNG.</summary>
    private static readonly byte[] TransparentPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89,
        0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54,
        0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,
        0x0D, 0x0A, 0x2D, 0xB4,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
        0xAE, 0x42, 0x60, 0x82,
    };

    // Declared after the bytes on purpose: static initializers run in the order they are written, and the
    // property reads the array — putting the property first built the bitmap from a null array and took the
    // application down while the main window was being constructed.
    public static Bitmap Transparent { get; } = CreateTransparent();

    private static Bitmap CreateTransparent() => new(new MemoryStream(TransparentPng));

    public static Bitmap? Resolve(string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return null;
        }

        var path = App.Host?.Assets.ResolvePath(assetId);
        var key = assetId;

        if (path is not null)
        {
            try
            {
                var info = new FileInfo(path);
                key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
            }
            catch (Exception)
            {
                // Not statable: keep the id as the key and let the load below decide.
            }
        }

        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Bitmap? bitmap = null;
        if (path is not null)
        {
            try
            {
                // Read through a stream so the file is not held open: the next replacement has to be able
                // to overwrite it while this bitmap is still on screen. The bytes are decoded through a
                // memory stream rather than the file's, because a bitmap that still refers to a closed
                // stream fails at draw time — inside the compositor, where a failure takes the whole
                // application down instead of one control.
                var bytes = File.ReadAllBytes(path);
                using var stream = new MemoryStream(bytes);
                bitmap = new Bitmap(stream);
            }
            catch (Exception)
            {
                // A corrupt or unsupported image must not take the page down.
                App.Host?.Log.Warn("asset.decode", $"Asset '{assetId}' could not be decoded: {path}");
                bitmap = null;
            }
        }

        // Bounded: this runs on every rebind, and a skin can be replaced any number of times.
        if (Cache.Count > 128)
        {
            Cache.Clear();
        }

        Cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>The character sheet the active skin declares, if it declares one.</summary>
    public static string? CharacterIdFor(SkinManifest? skin) => SkinAssets.IdFor(skin, SkinAssets.Character);

    /// <summary>
    /// The companion the active skin declares, if it declares one.
    ///
    /// <para>
    /// Eri's is 小黑, the black cat that follows her around. It is a slot of its
    /// own rather than a second frame of the character sheet, because a companion
    /// is a different thing: it has its own art, and a skin may have one, the other,
    /// or neither. A skin that declares none shows none — there is no stand-in.
    /// </para>
    /// </summary>
    public static string? CompanionIdFor(SkinManifest? skin) => SkinAssets.IdFor(skin, SkinAssets.Companion);

    /// <summary>The skin's brand mark, or null when it ships none.</summary>
    public static string? LogoIdFor(SkinManifest? skin) => SkinAssets.IdFor(skin, SkinAssets.Logo);

    /// <summary>The skin's empty-state art, or null when it ships none.</summary>
    public static string? EmptyStateIdFor(SkinManifest? skin) => SkinAssets.IdFor(skin, SkinAssets.EmptyState);

    /// <summary>The skin's loading art, or null when it ships none.</summary>
    public static string? LoadingStateIdFor(SkinManifest? skin) => SkinAssets.IdFor(skin, SkinAssets.LoadingState);
}
