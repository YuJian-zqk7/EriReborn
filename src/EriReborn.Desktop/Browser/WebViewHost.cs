using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using EriReborn.UI.Avalonia.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace EriReborn.Desktop.Browser;

/// <summary>
/// Hosts a WebView2 (the Edge/Chromium kernel Windows already ships) inside our own window, so a
/// platform's real sign-in page runs inside our shell.
///
/// <para>
/// This parents the kernel to the window's own HWND and places it by hand, rather than going through
/// Avalonia's <c>NativeControlHost</c>: that abstraction never materialised its native child here, so
/// the page could never start.
/// </para>
///
/// <para>
/// Diagnostics go next to the executable, because this machine denies the app writes to its per-user
/// folders — which is why earlier attempts produced a blank window and no evidence.
/// </para>
/// </summary>
public sealed class WebViewHost : Control, IBrowserHost
{
    private static readonly string DataRoot = ResolveDataRoot();

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private bool _started;
    private bool _useProxy = true;
    private System.Drawing.Rectangle _lastBounds = System.Drawing.Rectangle.Empty;

    /// <inheritdoc />
    public event EventHandler<string>? Failed;

    /// <inheritdoc />
    public event EventHandler<string>? CredentialFound;

    /// <summary>
    /// Cookie names that mean "this browser is signed in", per platform host fragment. Only 百度 was
    /// listed at first, so a completed sign-in at 123/夸克/迅雷/蓝奏 could never be noticed.
    /// </summary>
    private static readonly (string Host, string[] Names)[] CredentialCookies =
    {
        ("baidu.com", new[] { "BDUSS", "STOKEN" }),
        ("123pan.com", new[] { "authorToken", "token" }),
        ("quark.cn", new[] { "b-user-id", "__uid", "__kp", "__puus", "__pus" }),
        ("xunlei.com", new[] { "sessionid", "userid", "deviceid", "login_key" }),
        ("woozooo.com", new[] { "ylogin", "phpdisk_info", "uids" }),
        ("lanzou.com", new[] { "ylogin", "phpdisk_info", "uids" }),
    };

    /// <summary>Cookie-name hints that mark a session rather than page-load telemetry.</summary>
    private static readonly string[] CredentialHints =
    {
        "token", "auth", "session", "passport", "bduss", "stoken", "ticket", "login", "puus", "__kp", "phpdisk_info",
    };

    /// <summary>
    /// A page load drops plenty of tracking cookies (BAIDUID, H_PS_PSSID, ...). Counting those as a
    /// credential closed the window right after it opened, so a cookie counts only when the platform is
    /// known to set it at sign-in or its name carries a session meaning.
    /// </summary>
    private static bool LooksLikeCredential(string name, System.Collections.Generic.List<string> expected)
    {
        foreach (var known in expected)
        {
            if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var hint in CredentialHints)
        {
            if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 123 keeps its token in web storage rather than a cookie, so a cookie-only probe can never see
    /// that sign-in. This asks the page for token-shaped storage keys.
    /// </summary>
    private const string StorageProbeScript =
        "(function(){try{var re=/(token|auth|passport|bduss|ticket|session)/i;var out=[];" +
        "var push=function(s){if(!s)return;for(var i=0;i<s.length;i++){var k=s.key(i);" +
        "if(k&&re.test(k)){var v=s.getItem(k);if(v)out.push(k+'='+v);}}};" +
        "push(localStorage);push(sessionStorage);return out.join('; ');}catch(e){return '';}})()";

    /// <summary>True once a navigation has loaded, so a later redirect abort is not treated as fatal.</summary>
    private bool _navigationSucceeded;

    /// <summary>Set once a credential has been handed over, so we do not report the same one twice.</summary>
    private bool _credentialReported;

    /// <summary>
    /// The credential already present when this window opened. The browser profile is shared, so a
    /// previous sign-in is still there; reporting it closed the window the instant it opened, which
    /// looked exactly like "百度打不开".
    /// </summary>
    private string? _baselineCredential;

    private bool _baselineCaptured;

    /// <summary>True when a Chromium kernel can be hosted at all (Windows only).</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The page to load once the kernel is ready.</summary>
    public Uri? Source { get; set; }

    /// <summary>Raised for every response the page receives, so a collector can watch for a token.</summary>
    public event EventHandler<CoreWebView2WebResourceResponseReceivedEventArgs>? ResponseReceived;

    /// <summary>The live kernel; null until initialisation finishes.</summary>
    public CoreWebView2? Core => _core;

    public WebViewHost()
    {
        BrowserLog.Write("WebViewHost constructed (supported=" + IsSupported + ")");
    }

    /// <summary>Picks the first folder we can actually write to.</summary>
    private static string ResolveDataRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "EriReborn-webview", "data"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EriReborn", "webview"),
            Path.Combine(Path.GetTempPath(), "EriReborn", "webview"),
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
                // Try the next candidate.
            }
        }

        return candidates[^1];
    }

    /// <summary>
    /// True when Windows is told to use a proxy that nothing is listening on — the state a VPN client
    /// leaves behind when it is closed. WebView2 follows that setting and then fails with its own proxy
    /// error page, so in that case the kernel is asked to connect directly instead.
    ///
    /// <para>
    /// This is decided before the kernel starts on purpose: recreating a kernel mid-flight to switch
    /// route fails with a COM state error, and the kernel's error page must never reach the user.
    /// </para>
    /// </summary>
    private static bool SystemProxyLooksDead()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key?.GetValue("ProxyEnable") is not int enabled || enabled == 0)
            {
                return false;
            }

            if (key.GetValue("ProxyServer") is not string configured || string.IsNullOrWhiteSpace(configured))
            {
                return false;
            }

            // Formats seen in the wild: "host:port", "http://host:port" and "http=h:p;https=h:p".
            var raw = configured;
            var equals = raw.LastIndexOf('=');
            if (equals >= 0)
            {
                raw = raw[(equals + 1)..];
            }

            raw = raw.Trim().TrimEnd('/');
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[7..];
            }

            var parts = raw.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 80;
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            if (!connect.Wait(1500))
            {
                BrowserLog.Write("proxy probe: " + host + ":" + port + " timed out");
                return true;
            }

            client.Close();
            return false;
        }
        catch (Exception ex)
        {
            BrowserLog.Write("proxy probe failed (" + ex.GetType().Name + ") -> treating proxy as unusable");
            return true;
        }
    }

    private void Fail(string message)
    {
        BrowserLog.Write("FAILED: " + message);
        Failed?.Invoke(this, message);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BrowserLog.Write("attached to tree (bounds " + Bounds.Width + "x" + Bounds.Height + ")");

        if (OperatingSystem.IsWindows() && !_started)
        {
            _started = true;
            _ = InitializeAsync();
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        BrowserLog.Write("detached from tree; closing controller");
        try
        {
            _controller?.Close();
        }
        catch (Exception ex)
        {
            BrowserLog.Write("close failed: " + ex.Message);
        }

        _controller = null;
        _core = null;
        _started = false;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateBounds();
        return size;
    }

    /// <summary>Completes once the kernel is up (or has definitively failed), so callers can await it.</summary>
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Loads a URL in this browser, gives its scripts time to finish, and returns the resulting HTML.
    /// Challenge pages (蓝奏) only have real content after those scripts have run.
    /// </summary>
    public async Task<string?> RenderAsync(Uri url, CancellationToken cancellationToken = default)
    {
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
        if (_core is null)
        {
            return null;
        }

        var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            _core.NavigationCompleted -= OnCompleted;
            navigation.TrySetResult(args.IsSuccess);
        }

        _core.NavigationCompleted += OnCompleted;
        BrowserLog.Write("render " + url);
        _core.Navigate(url.ToString());
        await navigation.Task.WaitAsync(cancellationToken).ConfigureAwait(true);

        // The anti-bot script fetches and rewrites the document after the load event.
        await Task.Delay(1500, cancellationToken).ConfigureAwait(true);
        var raw = await _core.ExecuteScriptAsync("document.documentElement.outerHTML").ConfigureAwait(true);
        return DecodeScriptString(raw);
    }

    private async Task InitializeAsync()
    {
        try
        {
            var hwnd = IntPtr.Zero;
            for (var i = 0; i < 60 && hwnd == IntPtr.Zero; i++)
            {
                hwnd = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero)
                {
                    await Task.Delay(50).ConfigureAwait(true);
                }
            }

            BrowserLog.Write("top-level hwnd=0x" + hwnd.ToInt64().ToString("X"));
            if (hwnd == IntPtr.Zero)
            {
                Fail("拿不到窗口句柄，无法挂载浏览器内核。");
                return;
            }

            string version;
            try
            {
                version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                Fail("找不到 WebView2 运行时（Edge/Chromium 内核）：" + ex.Message);
                return;
            }

            var proxyDead = await Task.Run(SystemProxyLooksDead).ConfigureAwait(true);
            _useProxy = !proxyDead;
            BrowserLog.Write("runtime " + version + "; dataRoot=" + DataRoot
                + "; system proxy " + (proxyDead ? "UNUSABLE -> direct connection" : "in use"));

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = _useProxy ? string.Empty : "--no-proxy-server",
            };

            var environment = await CoreWebView2Environment.CreateAsync(null, DataRoot, options).ConfigureAwait(true);
            BrowserLog.Write("environment ready");

            _controller = await environment.CreateCoreWebView2ControllerAsync(hwnd).ConfigureAwait(true);
            BrowserLog.Write("controller ready");
            _core = _controller.CoreWebView2;
            _core.WebResourceResponseReceived += (_, args) => ResponseReceived?.Invoke(this, args);

            // Baidu opens its sign-in in a popup; keep it in our own view instead.
            _core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                BrowserLog.Write("popup redirected into our view: " + args.Uri);
                _core.Navigate(args.Uri);
            };

            _core.NavigationCompleted += (_, args) => _ = OnNavigationCompletedAsync(args);

            UpdateBounds();
            _controller.IsVisible = true;

            if (Source is not null)
            {
                BrowserLog.Write("navigating " + Source);
                _core.Navigate(Source.ToString());
            }

            _ready.TrySetResult();
        }
        catch (Exception ex)
        {
            Fail("浏览器内核初始化失败：" + ex.GetType().Name + " — " + ex.Message);
            _ready.TrySetResult();
        }
    }

    /// <summary>Handles a finished navigation: mask the page, or say plainly that it did not load.</summary>
    private async Task OnNavigationCompletedAsync(CoreWebView2NavigationCompletedEventArgs args)
    {
        BrowserLog.Write("navigation completed success=" + args.IsSuccess + " status=" + args.WebErrorStatus
            + " proxy=" + _useProxy);

        if (!args.IsSuccess)
        {
            // A redirect cancels the navigation it replaces. Single-page sign-in pages do this
            // constantly (123 signs in at /login, bounces to / and back), and reporting that as
            // "这一步没能打开登录页" hid a page that had loaded and was masking correctly.
            if (args.WebErrorStatus is CoreWebView2WebErrorStatus.ConnectionAborted
                or CoreWebView2WebErrorStatus.OperationCanceled || _navigationSucceeded)
            {
                BrowserLog.Write("navigation aborted after a load (" + args.WebErrorStatus + "); keeping the page");
                return;
            }

            // Never leave the kernel's own error page on screen.
            SetVisible(false);
            Fail("这一步没能打开登录页（" + args.WebErrorStatus + "）。"
                + (_useProxy ? "这次走的是系统代理" : "这次走的是直连")
                + "，检查一下这台机器的网络或代理设置后再试。");
            return;
        }

        _navigationSucceeded = true;
        await MaskLoginFormAsync();
        await CaptureCredentialAsync();
    }

    /// <summary>
    /// Watches for the cookie that appears once the user is actually signed in. Signing in is a round
    /// trip inside the page, and the user may need a minute, so this polls a few times per navigation
    /// and runs again on the next navigation (a completed sign-in redirects, which triggers one).
    /// </summary>
    private async Task CaptureCredentialAsync()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(3000).ConfigureAwait(true);
            }

            if (_core is null || Source is null || _credentialReported)
            {
                return;
            }

            try
            {
                var cookies = await _core.CookieManager.GetCookiesAsync(Source.ToString()).ConfigureAwait(true);
                var found = new System.Collections.Generic.List<string>();
                var expected = new System.Collections.Generic.List<string>(NamesFor(Source.Host));
                var expectedSeen = new System.Collections.Generic.List<string>();

                // A cookie-session platform (夸克, 蓝奏) needs the whole jar of its own host, and a guessed
                // name list missed sign-ins whose cookie we had not predicted. The change check below is
                // what keeps an already-signed-in profile from closing the window.
                foreach (var cookie in cookies)
                {
                    if (string.IsNullOrEmpty(cookie.Value) || !LooksLikeCredential(cookie.Name, expected))
                    {
                        continue;
                    }

                    found.Add(cookie.Name + "=" + cookie.Value);

                    foreach (var name in expected)
                    {
                        if (string.Equals(name, cookie.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            expectedSeen.Add(cookie.Name);
                            break;
                        }
                    }
                }

                var stored = DecodeScriptString(await _core.ExecuteScriptAsync(StorageProbeScript).ConfigureAwait(true));
                if (stored.Length > 0)
                {
                    found.Add(stored);
                }

                var signature = string.Join("; ", found);

                if (!_baselineCaptured)
                {
                    // First probe only records what was already here; reporting it would close the window
                    // before the user could even see the page.
                    _baselineCaptured = true;
                    _baselineCredential = signature;
                    if (signature.Length > 0)
                    {
                        BrowserLog.Write("already signed in when the window opened (" + expectedSeen.Count
                            + " known names); waiting for the credential to change");
                    }

                    continue;
                }

                if (signature.Length == 0 || signature == _baselineCredential)
                {
                    continue;
                }

                _credentialReported = true;

                // Counts and names only: the values are the user's secret and never belong in a log.
                BrowserLog.Write("credential changed after sign-in: " + found.Count + " items"
                    + (expectedSeen.Count > 0 ? ", known names: " + string.Join(", ", expectedSeen) : string.Empty));
                CredentialFound?.Invoke(this, signature);
                return;
            }
            catch (Exception ex)
            {
                BrowserLog.Write("credential probe failed: " + ex.GetType().Name);
                return;
            }
        }
    }

    /// <summary>The cookie names that count as a sign-in for the host currently loaded.</summary>
    private static System.Collections.Generic.IEnumerable<string> NamesFor(string host)
    {
        foreach (var (fragment, names) in CredentialCookies)
        {
            if (host.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var name in names)
                {
                    yield return name;
                }
            }
        }
    }

    /// <summary>ExecuteScriptAsync returns a JSON string literal; unwrap it defensively.</summary>
    private static string DecodeScriptString(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null")
        {
            return string.Empty;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return json.Trim().Trim('"');
        }
    }

    /// <summary>Shows or hides the page inside our window.</summary>
    public void SetVisible(bool visible)
    {
        if (_controller is not null)
        {
            _controller.IsVisible = visible;
        }
    }

    /// <summary>
    /// Hides the official site around the sign-in form. Sign-in pages are often single-page apps that
    /// render the form well after the navigation completes (123, Quark), so this keeps polling for
    /// roughly 21s instead of giving up after a few seconds; identical results are logged once.
    /// </summary>
    private async Task MaskLoginFormAsync()
    {
        string? previous = null;

        for (var attempt = 0; attempt < 26; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(800).ConfigureAwait(true);
            }

            if (_core is null)
            {
                return;
            }

            try
            {
                var result = await _core.ExecuteScriptAsync(LoginScripts.IsolateLoginForm).ConfigureAwait(true);
                if (!string.Equals(result, previous, StringComparison.Ordinal))
                {
                    BrowserLog.Write("mask attempt " + attempt + " -> " + result);
                    previous = result;
                }

                if (result.Contains("stripped"))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                BrowserLog.Write("mask failed: " + ex.Message);
                return;
            }
        }
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
        var rect = new System.Drawing.Rectangle(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            Math.Max(1, (int)Math.Round(Bounds.Width * scale)),
            Math.Max(1, (int)Math.Round(Bounds.Height * scale)));

        _controller.Bounds = rect;

        if (rect != _lastBounds)
        {
            _lastBounds = rect;
            BrowserLog.Write("bounds x=" + rect.X + " y=" + rect.Y + " " + rect.Width + "x" + rect.Height + " (scale " + scale + ")");
        }
    }
}
