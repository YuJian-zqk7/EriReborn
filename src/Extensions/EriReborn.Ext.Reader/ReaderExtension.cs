using EriReborn.Extension;
using EriReborn.Extension.Reader;

namespace EriReborn.Ext.Reader;

/// <summary>
/// Contributes the reader. The real reading happens in a window the host opens
/// over a shared <see cref="ReaderSession"/>; the inline page below is the entry
/// point and the fallback for hosts without windows — it renders the same session,
/// so progress and shelf stay in step no matter which surface is used.
/// </summary>
public sealed class ReaderExtension : IExtension
{
    public string Id => "erireborn_reader";

    public string DisplayName => "阅读器";

    public string Version => "2.3.0";

    public void Initialize(IExtensionHost host)
    {
        var session = new ReaderSession(host);
        host.RegisterPage(new ReaderPage(host, session));
    }
}

internal sealed class ReaderPage : IExtensionPage
{
    private readonly IExtensionHost _host;
    private readonly ReaderSession _session;

    /// <summary>Where the page was created; body updates are marshalled back to it.
    /// Captured lazily on the first UI-thread call — at startup-load time no dispatcher
    /// context exists yet, so the constructor would only ever record null.</summary>
    private SynchronizationContext? _context;

    private string _body = "还没有打开任何书。点「打开阅读器窗口」用完整阅读器（书架 / 目录 / 书签）；"
        + "或直接「打开文件」在这里快速阅读。";

    private bool _loading;

    public ReaderPage(IExtensionHost host, ReaderSession session)
    {
        _host = host;
        _session = session;
        _context = SynchronizationContext.Current;

        _session.CurrentBookChanged += (_, _) => _ = RefreshBodyAsync();
    }

    public string Key => "reader";

    public string Title => "阅读";

    public string Body => _body;

    public event EventHandler? BodyChanged;

    public IReadOnlyList<ExtensionPageAction> Actions => new[]
    {
        new ExtensionPageAction("打开阅读器窗口", OpenWindow, IsPrimary: true),
        new ExtensionPageAction("打开文件", OpenFile),
        new ExtensionPageAction("上一页", PreviousPage),
        new ExtensionPageAction("下一页", NextPage),
    };

    private void OpenWindow()
    {
        if (_host.OpenReaderWindow(_session))
        {
            return;
        }

        // No window system here (single-view host): say so instead of doing nothing.
        SetBody("这个宿主不支持独立窗口，请用「打开文件」在页面内阅读。");
    }

    private async void OpenFile()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            SetBody("正在打开…");
            var book = await _session.OpenFromPickerAsync().ConfigureAwait(true);
            if (book is null)
            {
                SetBody("没有选择文件。");
                return;
            }

            await RefreshBodyAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _host.Log("error", $"阅读器：打开失败：{ex}");
            SetBody($"打开失败：{ex.Message}");
        }
        finally
        {
            _loading = false;
        }
    }

    private async void PreviousPage()
    {
        if (_session.TotalPages == 0 || _session.CurrentPageIndex <= 0)
        {
            return;
        }

        await JumpTo(_session.CurrentPageIndex - 1).ConfigureAwait(true);
    }

    private async void NextPage()
    {
        if (_session.TotalPages == 0 || _session.CurrentPageIndex >= _session.TotalPages - 1)
        {
            return;
        }

        await JumpTo(_session.CurrentPageIndex + 1).ConfigureAwait(true);
    }

    private async Task JumpTo(int pageIndex)
    {
        var page = await _session.GetPageAsync(pageIndex).ConfigureAwait(true);
        if (page is null)
        {
            return;
        }

        await _session.SaveProgressAsync(page.PageIndex).ConfigureAwait(true);
        SetBody($"《{page.BookTitle}》  第 {page.PageIndex + 1} / {page.TotalPages} 页\n\n{page.Text}");
    }

    private async Task RefreshBodyAsync()
    {
        var page = await _session.GetPageAsync(_session.CurrentPageIndex).ConfigureAwait(true);
        if (page is null)
        {
            return;
        }

        SetBody($"《{page.BookTitle}》  第 {page.PageIndex + 1} / {page.TotalPages} 页\n\n{page.Text}");
    }

    private void SetBody(string body)
    {
        // The first call always happens on the UI thread (the button click), which is
        // where the shell's context finally becomes visible — grab it there.
        if (SynchronizationContext.Current is { } current && !ReferenceEquals(current, _context))
        {
            _context = current;
        }

        // Bindings must be told on the thread the shell was built on; a load finishing
        // on a worker thread would otherwise poke the UI from the wrong side, where the
        // update is at best ignored and at worst poisons the render loop.
        if (_context is null || SynchronizationContext.Current == _context)
        {
            _body = body;
            BodyChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _context.Post(_ =>
            {
                _body = body;
                BodyChanged?.Invoke(this, EventArgs.Empty);
            }, null);
        }
    }
}
