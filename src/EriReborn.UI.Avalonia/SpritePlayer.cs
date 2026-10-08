using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace EriReborn.UI.Avalonia;

/// <summary>
/// Plays a sheet's sliced frames at the rate the manifest declares (spec 51).
/// A sheet with a single frame is simply shown; frames that fail to decode are
/// skipped rather than replaced by a placeholder.
/// </summary>
public sealed class SpritePlayer : IDisposable
{
    private readonly Image _target;
    private readonly List<Bitmap> _frames = new();
    private readonly DispatcherTimer? _timer;
    private int _index;

    private SpritePlayer(Image target, string assetId)
    {
        _target = target;

        var host = App.Host;
        if (host is null)
        {
            return;
        }

        foreach (var path in host.Assets.ResolveFrames(assetId))
        {
            try
            {
                _frames.Add(new Bitmap(path));
            }
            catch (Exception)
            {
                host.Log.Warn("asset.decode", $"Sprite frame could not be decoded: {path}");
            }
        }

        if (_frames.Count == 0)
        {
            return;
        }

        _target.Source = _frames[0];

        var sprite = host.Assets.Resolve(assetId)?.Sprite;
        if (_frames.Count < 2)
        {
            return;
        }

        var fps = sprite is { Fps: > 0 } ? sprite.Fps : 8;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / fps) };
        _timer.Tick += (_, _) => Advance(sprite?.Loop ?? true);
    }

    public int FrameCount => _frames.Count;

    /// <summary>Returns null when the sheet has no usable frames.</summary>
    public static SpritePlayer? Create(Image target, string? assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            return null;
        }

        var player = new SpritePlayer(target, assetId);
        return player.FrameCount == 0 ? null : player;
    }

    public void Start() => _timer?.Start();

    public void Stop() => _timer?.Stop();

    private void Advance(bool loop)
    {
        if (_frames.Count == 0)
        {
            return;
        }

        _index++;
        if (_index >= _frames.Count)
        {
            if (!loop)
            {
                _timer?.Stop();
                return;
            }

            _index = 0;
        }

        _target.Source = _frames[_index];
    }

    /// <summary>
    /// Stops the animation and lets go of the frames — without disposing them.
    ///
    /// <para>
    /// They used to be disposed, and that is what closed EriReborn when a skin was switched: this player
    /// hands its frames straight to the <see cref="Image"/>'s Source, and the compositor may be drawing the
    /// very frame that was just disposed. A disposed <c>Bitmap</c> has no platform picture left, so
    /// Avalonia's own <c>Image.Render</c> throws inside the render pass — where the exception leaves the
    /// dispatcher and takes the process down instead of one control. Dropping the last reference is what is
    /// actually needed; the frames are collected once nothing draws them.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        _timer?.Stop();

        // Parked on the shared transparent pixel first, so a control whose character is going away stops
        // showing the one that is no longer there.
        if (_target.Source is not null)
        {
            _target.Source = SkinArtwork.Transparent;
        }

        _frames.Clear();
    }
}
