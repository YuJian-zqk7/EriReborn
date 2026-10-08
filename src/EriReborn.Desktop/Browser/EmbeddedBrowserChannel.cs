using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EriReborn.Cloud;

namespace EriReborn.Desktop.Browser;

/// <summary>
/// Renders pages in a hidden WebView2 window. This is what makes 蓝奏's JavaScript challenge and
/// 迅雷's captcha-minted pages readable at all: a plain HTTP client only ever sees the challenge.
/// </summary>
internal sealed class EmbeddedBrowserChannel : ICloudBrowserChannel
{
    private Window? _window;
    private WebViewHost? _host;

    /// <summary>True when this build can render at all; the kernel reports its own failure into the log.</summary>
    public bool IsAvailable => true;

    public async Task<string?> FetchRenderedHtmlAsync(string url, CancellationToken cancellationToken = default)
    {
        // WebView2 is a UI-thread citizen, so the render runs where the window lives.
        return await Dispatcher.UIThread.InvokeAsync(() => RenderAsync(url, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<string?> RenderAsync(string url, CancellationToken cancellationToken)
    {
        EnsureWindow();
        return _host is null
            ? null
            : await _host.RenderAsync(new Uri(url), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// One reusable off-screen window: creating a browser kernel per render would cost seconds and a
    /// new profile lock each time.
    /// </summary>
    private void EnsureWindow()
    {
        if (_host is not null)
        {
            return;
        }

        _host = new WebViewHost();
        _window = new Window
        {
            Title = "EriReborn · 页面渲染",
            Width = 1100,
            Height = 820,
            Position = new PixelPoint(-3600, -3600),
            ShowInTaskbar = false,
            Content = _host,
        };

        _window.Show();
    }
}
