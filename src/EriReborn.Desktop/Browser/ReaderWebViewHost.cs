using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using EriReborn.UI.Avalonia.Controls;
using Microsoft.Web.WebView2.Core;

namespace EriReborn.Desktop.Browser;

/// <summary>
/// 阅读器正文区的 WebView2 内核：只渲染本地 HTML，不打开任何远程页面。
///
/// <para>
/// 挂载方式与 <see cref="WebViewHost"/> 相同——手动挂到顶层窗口 HWND 并按布局
/// 定位，因为 Avalonia 的 NativeControlHost 在本项目里始终无法 materialize。
/// 区别在于这里不做导航/cookie/代理探测：内核就绪后只等宿主用
/// <see cref="LoadHtml"/> 灌一次 HTML 壳，之后全部走 JS 增量推送。
/// </para>
/// </summary>
public sealed class ReaderWebViewHost : Control, IReaderWebView
{
    private static readonly string DataRoot = ResolveDataRoot();

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _started;
    private bool _ready;
    private System.Drawing.Rectangle _lastBounds = System.Drawing.Rectangle.Empty;

    public event EventHandler? Ready;

    public event EventHandler<string>? WebMessage;

    public ReaderWebViewHost()
    {
        BrowserLog.Write("ReaderWebViewHost constructed");
    }

    /// <summary>Picks the first folder we can actually write to; keeps its own profile apart
    /// from the sign-in browser's.</summary>
    private static string ResolveDataRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "EriReborn-webview", "reader"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EriReborn", "webview-reader"),
            Path.Combine(Path.GetTempPath(), "EriReborn", "webview-reader"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".writable");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                // Try the next.
            }
        }

        return candidates[^1];
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (OperatingSystem.IsWindows() && !_started)
        {
            _started = true;
            _ = InitializeAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        try
        {
            _controller?.Close();
        }
        catch (Exception ex)
        {
            BrowserLog.Write("reader close failed: " + ex.Message);
        }

        _controller = null;
        _core = null;
        _started = false;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateBounds();
        return size;
    }

    public void LoadHtml(string html)
    {
        // 阅读器内核就绪时已自动加载随包页面；这个方法留给接口完整性与将来的自定义页面。
        _core?.NavigateToString(html);
    }

    public async Task<string?> EvaluateAsync(string javascript)
    {
        if (_core is null || !_ready)
        {
            BrowserLog.Write("EvaluateAsync: skip core=" + (_core is null ? "null" : "set") + " ready=" + _ready);
            return null;
        }

        var result = await _core.ExecuteScriptAsync(javascript).ConfigureAwait(true);
        BrowserLog.Write("EvaluateAsync: result=" + (result ?? "null") + " (len=" + javascript.Length + ")");
        return result;
    }

    private async Task InitializeAsync()
    {
        try
        {
            _ready = false;
            var hwnd = IntPtr.Zero;
            for (var i = 0; i < 60 && hwnd == IntPtr.Zero; i++)
            {
                hwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero)
                {
                    await Task.Delay(50).ConfigureAwait(true);
                }
            }

            if (hwnd == IntPtr.Zero)
            {
                BrowserLog.Write("reader: no top-level hwnd");
                return;
            }

            try
            {
                _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                BrowserLog.Write("reader: WebView2 runtime missing: " + ex.Message);
                return;
            }

            var environment = await CoreWebView2Environment.CreateAsync(null, DataRoot).ConfigureAwait(true);
            _controller = await environment.CreateCoreWebView2ControllerAsync(hwnd).ConfigureAwait(true);
            _core = _controller.CoreWebView2;

            // 阅读页是自己的工具条，不要浏览器右键菜单；缩放也关掉，避免页面缩放
            // 打乱选择坐标（翻页/字号全走我们自己的设置）。
            _core.Settings.AreDefaultContextMenusEnabled = false;
            _core.Settings.IsZoomControlEnabled = false;
            _core.Settings.IsStatusBarEnabled = false;

            _core.WebMessageReceived += OnWebMessageReceived;

            UpdateBounds();
            _controller.IsVisible = true;

            BrowserLog.Write("reader kernel ready");

            // 自动灌入随包的阅读页；页面脚本加载完会回 domReady，那时才对外宣布 Ready。
            _core.NavigateToString(LoadReaderPageHtml());
        }
        catch (Exception ex)
        {
            BrowserLog.Write("reader init failed: " + ex.GetType().Name + " — " + ex.Message);
        }
    }

    /// <summary>阅读页 HTML 是随程序集打包的嵌入资源，发布后不依赖磁盘上的文件。</summary>
    private static string LoadReaderPageHtml()
    {
        const string resourceName = "EriReborn.Desktop.Browser.ReaderPage.html";
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("找不到阅读页嵌入资源：" + resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 页面统一 postMessage(JSON.stringify(payload))；优先按字符串取，失败再退回原始 JSON。
        string? message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch
        {
            message = e.WebMessageAsJson;
        }

        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        // DOM 就绪握手只用来翻转 Ready，不往上传。
        if (!_ready && message.Contains("\"domReady\"", StringComparison.Ordinal))
        {
            _ready = true;
            BrowserLog.Write("reader page DOM ready");
            Ready?.Invoke(this, EventArgs.Empty);
            return;
        }

        WebMessage?.Invoke(this, message);
    }

    private void UpdateBounds()
    {
        if (_controller is null)
        {
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var scale = top.RenderScaling;
        var origin = this.TranslatePoint(new Point(0, 0), top) ?? new Point(0, 0);
        var w = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        var h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        var bounds = new System.Drawing.Rectangle(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            w,
            h);
        if (bounds != _lastBounds)
        {
            BrowserLog.Write("bounds x=" + bounds.X + " y=" + bounds.Y + " " + bounds.Width + "x" + bounds.Height + " (scale " + scale + ")");
        }
        _controller.Bounds = bounds;
        _lastBounds = bounds;
    }
}
