using System;
using System.Threading.Tasks;

namespace EriReborn.UI.Avalonia.Controls;

/// <summary>
/// 阅读器正文区的浏览器内核抽象。共享 UI（net8.0）不能引用 WebView2，
/// 具体实现由 Windows 端在启动时经 <see cref="BrowserSlot.ReaderFactory"/> 注入。
///
/// <para>
/// 正文用 HTML 渲染：文本排版、选词、高亮底色全部交给浏览器内核，避开
/// Avalonia 原生文本控件在动态内容下的排版缺陷。宿主只负责把页面数据推下去，
/// 以及把页面上报的选词/划线消息接回来。
/// </para>
/// </summary>
public interface IReaderWebView
{
    /// <summary>内核完成首屏初始化，可以接收推送。</summary>
    event EventHandler? Ready;

    /// <summary>
    /// 页面通过 <c>window.chrome.webview.postMessage(...)</c> 上报的消息（原样 JSON 字符串）。
    /// </summary>
    event EventHandler<string>? WebMessage;

    /// <summary>加载整页 HTML（仅首屏一次；之后用 <see cref="EvaluateAsync"/> 增量推送）。</summary>
    void LoadHtml(string html);

    /// <summary>在页面里执行一段 JS；返回其 JSON 形式的结果（不需要结果时可丢弃）。</summary>
    Task<string?> EvaluateAsync(string javascript);
}
