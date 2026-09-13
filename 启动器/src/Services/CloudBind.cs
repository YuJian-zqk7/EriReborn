using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace SetupLauncher.Services
{
    /// <summary>
    /// 内嵌 Chromium (WebView2) 的 123 云盘登录宿主。
    ///
    /// 这个类是唯一触碰 WebView2 类型的地方 —— 托管 DLL 缺失时, 对 Attach 的调用
    /// 会在 JIT 阶段抛 FileNotFoundException, 调用方 catch 后走"外部浏览器登录"回退。
    ///
    /// cookie 只按 123pan 域过滤收集 (与 引擎\网盘登录.ps1 同一策略),
    /// 绝不把其它站点的登录态发给第三方。轮询走 DevTools 协议 (纯托管 Task,
    /// 不依赖 WinRT 互操作)。
    /// </summary>
    public static class WebView2Host
    {
        public static void Attach(Grid host, string url, string userDataFolder,
                                  Action<string> onCookies, Action<string> onStatus,
                                  out Action dispose)
        {
            var webView = new WebView2();
            host.Children.Add(webView);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            var ready = false;
            var busy = false;
            var lastCookie = "";

            timer.Tick += async (s, e) =>
            {
                if (!ready || busy || webView.CoreWebView2 == null) return;
                busy = true;
                try
                {
                    // Network.getAllCookies + 按 123pan 域过滤 (不碰其它域的 cookie)
                    var json = await webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
                        "Network.getAllCookies", "{}");
                    var cookie = ExtractPanCookie(json);
                    if (cookie.Length > 20 && cookie != lastCookie)
                    {
                        lastCookie = cookie;
                        if (onCookies != null) onCookies(cookie);
                    }
                }
                catch (Exception ex)
                {
                    if (onStatus != null) onStatus("读取 cookie 失败: " + ex.Message);
                }
                finally { busy = false; }
            };

            RoutedEventHandler onLoaded = null;
            onLoaded = async (s, e) =>
            {
                webView.Loaded -= onLoaded;
                try
                {
                    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder);
                    await webView.EnsureCoreWebView2Async(env);
                    webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    webView.Source = new Uri(url);
                    ready = true;
                    timer.Start();
                    if (onStatus != null) onStatus("内嵌浏览器已就绪 —— 请在页面里登录 123 云盘，登录成功后会自动抓取登录态并验证。");
                }
                catch (Exception ex)
                {
                    if (onStatus != null) onStatus("WebView2 初始化失败: " + ex.Message);
                }
            };
            webView.Loaded += onLoaded;

            dispose = () =>
            {
                try { timer.Stop(); } catch { }
                try { webView.Dispose(); } catch { }
            };
        }

        /// <summary>从 Network.getAllCookies 的 JSON 里抽出 123pan 域的整条 cookie。</summary>
        private static string ExtractPanCookie(string json)
        {
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var d = (Dictionary<string, object>)ser.DeserializeObject(json);
                var arr = d.ContainsKey("cookies") ? d["cookies"] as object[] : null;
                if (arr == null) return "";
                var sb = new StringBuilder();
                foreach (var item in arr)
                {
                    var c = item as Dictionary<string, object>;
                    if (c == null) continue;
                    var dom = c.ContainsKey("domain") ? Convert.ToString(c["domain"]) : "";
                    if (dom.IndexOf("123pan", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var name = c.ContainsKey("name") ? Convert.ToString(c["name"]) : "";
                    var value = c.ContainsKey("value") ? Convert.ToString(c["value"]) : "";
                    if (name.Length == 0) continue;
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(name).Append('=').Append(value);
                }
                return sb.ToString();
            }
            catch { return ""; }
        }
    }
}
