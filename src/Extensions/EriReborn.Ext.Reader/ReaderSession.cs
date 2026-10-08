using System.Security.Cryptography;
using System.Text;
using EriReborn.Extension;
using EriReborn.Extension.Reader;

namespace EriReborn.Ext.Reader;

/// <summary>
/// The reader's brain, UI-free. Owns the shelf, the current book's parsed text,
/// its pagination and every saved bit of state. The window and the inline page are
/// both just views over this session — which is what lets the same reading state
/// survive switching between them, and a restart.
/// </summary>
public sealed class ReaderSession : IReaderSession
{
    private readonly IExtensionHost _host;
    private readonly ReaderStorage _storage;

    private List<ReaderBookInfo> _shelf = new();
    private Dictionary<string, ReaderProgress> _progress = new(StringComparer.Ordinal);
    private Dictionary<string, List<ReaderBookmark>> _bookmarks = new(StringComparer.Ordinal);
    private Dictionary<string, List<ReaderHighlight>> _highlights = new(StringComparer.Ordinal);

    private ReaderSettings _settings = ReaderSettings.Default;

    private ReaderBookInfo? _currentBook;
    private ParsedBook? _currentParsed;
    private List<PageSlice> _pages = new();
    private int _pageIndex;

    /// <summary>Guards the load path: two opens racing would interleave parses.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReaderSession(IExtensionHost host)
    {
        _host = host;
        _storage = new ReaderStorage(host.GetDataDirectory("erireborn_reader"));

        if (_storage.Available)
        {
            _shelf = _storage.LoadLibrary();
            _progress = _storage.LoadProgress();
            _bookmarks = _storage.LoadBookmarks();
            _highlights = _storage.LoadHighlights();
            _settings = _storage.LoadSettings();
            _host.Log("info", $"阅读器：已载入书架 {_shelf.Count} 本，设置（字号 {_settings.FontSize}）。");
        }
        else
        {
            _host.Log("warn", "阅读器：宿主未提供数据目录，本次阅读状态不保存。");
        }
    }

    public IReadOnlyList<ReaderBookInfo> Bookshelf => _shelf;

    public event EventHandler? BookshelfChanged;

    public ReaderBookInfo? CurrentBook => _currentBook;

    public IReadOnlyList<ReaderChapter> Chapters { get; private set; } = Array.Empty<ReaderChapter>();

    public IReadOnlyList<ReaderBookmark> Bookmarks
        => _currentBook is { } book && _bookmarks.TryGetValue(book.Id, out var marks)
            ? marks
            : (IReadOnlyList<ReaderBookmark>)Array.Empty<ReaderBookmark>();

    public IReadOnlyList<ReaderHighlight> Highlights
        => _currentBook is { } book && _highlights.TryGetValue(book.Id, out var spans)
            ? spans
            : (IReadOnlyList<ReaderHighlight>)Array.Empty<ReaderHighlight>();

    public ReaderSettings Settings => _settings;

    public event EventHandler? CurrentBookChanged;

    public event EventHandler? SettingsChanged;

    public event EventHandler? BookmarksChanged;

    public event EventHandler? HighlightsChanged;

    public int TotalPages => _pages.Count;

    public int CurrentPageIndex => _pageIndex;

    public async Task<ReaderBookInfo?> OpenFromPickerAsync(CancellationToken ct = default)
    {
        var path = await _host.PickFileAsync(
            "选择电子书",
            "电子书 (*.txt;*.epub)|*.txt;*.epub|文本文件 (*.txt)|*.txt|EPUB (*.epub)|*.epub")
            .ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return await LoadBookAsync(path, resumeProgress: true, addToShelf: true, ct).ConfigureAwait(true);
    }

    public async Task<ReaderBookInfo?> OpenFromBookshelfAsync(string bookId, CancellationToken ct = default)
    {
        var info = _shelf.FirstOrDefault(book => book.Id == bookId);
        if (info is null || !File.Exists(info.FilePath))
        {
            _host.Log("warn", $"阅读器：书架里的 {bookId} 文件已不在磁盘上。");
            return null;
        }

        return await LoadBookAsync(info.FilePath, resumeProgress: true, addToShelf: false, ct).ConfigureAwait(true);
    }

    public Task RemoveFromBookshelfAsync(string bookId)
    {
        _shelf.RemoveAll(book => book.Id == bookId);
        _progress.Remove(bookId);
        _bookmarks.Remove(bookId);
        _highlights.Remove(bookId);
        PersistShelf();
        PersistHighlights();
        BookshelfChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task<ReaderPageContent?> GetPageAsync(int pageIndex, CancellationToken ct = default)
    {
        if (_currentParsed is null || _pages.Count == 0)
        {
            return null;
        }

        var clamped = Math.Clamp(pageIndex, 0, _pages.Count - 1);
        var slice = _pages[clamped];
        var (displayStart, pageText) = slice.DisplaySlice(_currentParsed.FullText);

        var onThisPage = new List<ReaderBookmark>();
        var onPage = new List<ReaderPageHighlight>();
        if (_currentBook is { } book)
        {
            if (_bookmarks.TryGetValue(book.Id, out var marks))
            {
                onThisPage = marks.Where(mark => mark.PageIndex == clamped).ToList();
            }

            // 划线存的是全书偏移；换算成页面内下标，落在页外的部分裁掉。
            if (_highlights.TryGetValue(book.Id, out var spans))
            {
                foreach (var span in spans)
                {
                    var start = Math.Clamp(span.Start - displayStart, 0, pageText.Length);
                    var end = Math.Clamp(span.End - displayStart, 0, pageText.Length);
                    if (end - start < 1)
                    {
                        continue;
                    }

                    onPage.Add(new ReaderPageHighlight(span.Id, start, end, span.Note));
                }
            }
        }

        return await Task.FromResult(new ReaderPageContent(
            clamped,
            _pages.Count,
            pageText,
            _currentBook?.Title ?? string.Empty,
            onThisPage,
            onPage,
            displayStart)).ConfigureAwait(true);
    }

    public async Task SaveProgressAsync(int pageIndex)
    {
        if (_currentBook is not { } book || _pages.Count == 0)
        {
            return;
        }

        var clamped = Math.Clamp(pageIndex, 0, _pages.Count - 1);
        _progress[book.Id] = new ReaderProgress(clamped, _pages.Count, DateTimeOffset.Now);
        await Task.Run(PersistProgress).ConfigureAwait(false);
    }

    public async Task<ReaderBookmark> AddBookmarkAsync(int pageIndex, string? quote, string? note)
    {
        if (_currentBook is null)
        {
            throw new InvalidOperationException("没有打开的书，无法加书签。");
        }

        var mark = new ReaderBookmark(
            Guid.NewGuid().ToString("N"),
            Math.Max(0, pageIndex),
            string.IsNullOrWhiteSpace(quote) ? null : quote.Trim(),
            string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            DateTimeOffset.Now);

        if (!_bookmarks.TryGetValue(_currentBook.Id, out var list))
        {
            list = new List<ReaderBookmark>();
            _bookmarks[_currentBook.Id] = list;
        }

        list.Add(mark);
        await Task.Run(PersistBookmarks).ConfigureAwait(false);
        BookmarksChanged?.Invoke(this, EventArgs.Empty);
        return mark;
    }

    public async Task RemoveBookmarkAsync(string bookmarkId)
    {
        if (_currentBook is null)
        {
            return;
        }

        if (_bookmarks.TryGetValue(_currentBook.Id, out var list))
        {
            list.RemoveAll(mark => mark.Id == bookmarkId);
            await Task.Run(PersistBookmarks).ConfigureAwait(false);
            BookmarksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<ReaderHighlight> AddHighlightAsync(int pageIndex, int relStart, int relEnd, string? note)
    {
        if (_currentBook is null || _currentParsed is null || _pages.Count == 0)
        {
            throw new InvalidOperationException("没有打开的书，无法划线。");
        }

        var clampedPage = Math.Clamp(pageIndex, 0, _pages.Count - 1);
        var (displayStart, pageText) = _pages[clampedPage].DisplaySlice(_currentParsed.FullText);

        // 选区下标是页面文本内的，先换算成全书偏移，再去掉两端空白。
        var text = _currentParsed.FullText;
        var start = displayStart + Math.Clamp(relStart, 0, pageText.Length);
        var end = displayStart + Math.Clamp(relEnd, 0, pageText.Length);
        if (end < start)
        {
            (start, end) = (end, start);
        }

        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end <= start)
        {
            throw new ArgumentException("选区为空，无法划线。");
        }

        if (!_highlights.TryGetValue(_currentBook.Id, out var list))
        {
            list = new List<ReaderHighlight>();
            _highlights[_currentBook.Id] = list;
        }

        ReaderHighlight result;
        var existing = list.Find(span => span.Start == start && span.End == end);
        if (existing is not null)
        {
            // 同一段重复划线只更新笔记，不叠成双层。
            result = existing with { Note = string.IsNullOrWhiteSpace(note) ? existing.Note : note.Trim() };
            list[list.IndexOf(existing)] = result;
        }
        else
        {
            var quote = text[start..end];
            if (quote.Length > 200)
            {
                quote = quote[..200] + "…";
            }

            result = new ReaderHighlight(
                Guid.NewGuid().ToString("N"),
                start,
                end,
                quote,
                string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                DateTimeOffset.Now);

            // 按位置插入，标注面板自然按阅读顺序排列。
            var index = list.FindIndex(span => span.Start > start);
            if (index < 0)
            {
                list.Add(result);
            }
            else
            {
                list.Insert(index, result);
            }
        }

        await Task.Run(PersistHighlights).ConfigureAwait(false);
        HighlightsChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async Task RemoveHighlightAsync(string highlightId)
    {
        if (_currentBook is null)
        {
            return;
        }

        if (_highlights.TryGetValue(_currentBook.Id, out var list)
            && list.RemoveAll(span => span.Id == highlightId) > 0)
        {
            await Task.Run(PersistHighlights).ConfigureAwait(false);
            HighlightsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int PageIndexForOffset(int offset)
    {
        // 二分找包含该偏移的页，与版式调整后保位置用的是同一个思路。
        var low = 0;
        var high = _pages.Count - 1;
        var found = 0;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (_pages[mid].Start <= offset)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    /// <summary>搜索上下文每侧保留的字符数；足够认出句子，又不撑爆结果行。</summary>
    private const int SearchContextChars = 18;

    /// <summary>结果上限：一次搜出几千条对读者没有意义，还会拖慢面板渲染。</summary>
    private const int SearchMaxMatches = 1000;

    public Task<IReadOnlyList<ReaderSearchMatch>> SearchAsync(string query, CancellationToken ct = default)
    {
        var parsed = _currentParsed;
        if (parsed is null || _pages.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult<IReadOnlyList<ReaderSearchMatch>>(Array.Empty<ReaderSearchMatch>());
        }

        var needle = query.Trim();
        var text = parsed.FullText;

        return Task.Run<IReadOnlyList<ReaderSearchMatch>>(() =>
        {
            var results = new List<ReaderSearchMatch>();
            var position = 0;
            while (results.Count < SearchMaxMatches && ct.IsCancellationRequested == false)
            {
                var hit = text.IndexOf(needle, position, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    break;
                }

                var contextStart = Math.Max(0, hit - SearchContextChars);
                var contextEnd = Math.Min(text.Length, hit + needle.Length + SearchContextChars);
                var prefix = contextStart < hit ? "…" + Flatten(text[contextStart..hit]) : string.Empty;
                var suffix = contextEnd > hit + needle.Length ? Flatten(text[(hit + needle.Length)..contextEnd]) + "…" : string.Empty;

                results.Add(new ReaderSearchMatch(
                    results.Count + 1,
                    hit,
                    needle.Length,
                    PageIndexForOffset(hit),
                    prefix,
                    Flatten(text[hit..(hit + needle.Length)]),
                    suffix));

                position = hit + needle.Length;
            }

            return results;
        }, ct);
    }

    /// <summary>片段里的换行/制表符压成空格，保证一条结果始终是一行。</summary>
    private static string Flatten(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '\r' or '\n' or '\t' or '　')
            {
                chars[i] = ' ';
            }
        }

        return new string(chars).Trim();
    }

    public async Task UpdateSettingsAsync(ReaderSettings next)
    {
        var old = _settings;
        _settings = next;
        await Task.Run(() => _storage.SaveSettings(next)).ConfigureAwait(false);
        SettingsChanged?.Invoke(this, EventArgs.Empty);

        // A layout change reshuffles pagination. The position is kept by character
        // offset: where on the page the reader was, not which page number it was.
        if (_currentParsed is not null && _pages.Count > 0
            && (Math.Abs(old.FontSize - next.FontSize) > 0.01 || Math.Abs(old.LineHeight - next.LineHeight) > 0.01))
        {
            var offset = _pages[Math.Clamp(_pageIndex, 0, _pages.Count - 1)].Start;
            _pages = await Task.Run(() => BookParser.Paginate(_currentParsed.FullText, BookParser.PageCharBudget(next)))
                .ConfigureAwait(false);
            Chapters = BookParser.BuildChapters(_currentParsed, _pages);

            var low = 0;
            var high = _pages.Count - 1;
            var found = 0;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (_pages[mid].Start <= offset)
                {
                    found = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            _pageIndex = found;
            _host.Log("info", $"阅读器：版式调整后重新分页为 {_pages.Count} 页，阅读位置已保留。");
            CurrentBookChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The one load path for both entry points. Parsing runs off-thread; the state
    /// updates and events come back to the caller's context, which is the UI thread
    /// for every real caller.
    /// </summary>
    private async Task<ReaderBookInfo?> LoadBookAsync(string path, bool resumeProgress, bool addToShelf, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var info = await Task.Run(() =>
            {
                var format = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".epub" => "epub",
                    _ => "txt",
                };
                return new ReaderBookInfo(IdFor(path), Path.GetFileNameWithoutExtension(path), path, format,
                    new FileInfo(path).Length, DateTimeOffset.Now);
            }).ConfigureAwait(true);

            void Log(string level, string message) => _host.Log(level, message);

            var parsed = await Task.Run(
                () => info.Format == "epub"
                    ? BookParser.ParseEpub(path, Log)
                    : BookParser.ParseTxt(path, Log),
                ct).ConfigureAwait(true);

            if (parsed.FullText.Length == 0)
            {
                _host.Log("warn", $"阅读器：{path} 解析不出内容。");
                return null;
            }

            _currentBook = info;
            _currentParsed = parsed;
            _pages = await Task.Run(
                () => BookParser.Paginate(parsed.FullText, BookParser.PageCharBudget(_settings)), ct)
                .ConfigureAwait(true);
            Chapters = BookParser.BuildChapters(parsed, _pages);

            // The shelf entry is refreshed rather than duplicated when the book was
            // on it already, so one file stays one shelf row no matter how many
            // times it is reopened.
            var existing = _shelf.FindIndex(book => book.Id == info.Id);
            if (existing >= 0)
            {
                _shelf[existing] = info;
            }
            else if (addToShelf)
            {
                _shelf.Add(info);
            }

            if (resumeProgress && _progress.TryGetValue(info.Id, out var saved) && saved.PageIndex < _pages.Count)
            {
                _pageIndex = saved.PageIndex;
                _host.Log("info", $"阅读器：恢复《{info.Title}》到第 {_pageIndex + 1} / {_pages.Count} 页。");
            }
            else
            {
                _pageIndex = 0;
            }

            if (_storage.Available)
            {
                await Task.Run(PersistShelf).ConfigureAwait(false);
            }

            _host.Log("info", $"阅读器：《{info.Title}》共 {_pages.Count} 页，{Chapters.Count} 个章节。");
            BookshelfChanged?.Invoke(this, EventArgs.Empty);
            CurrentBookChanged?.Invoke(this, EventArgs.Empty);
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A stable identity for a book file: hash of its full path.</summary>
    internal static string IdFor(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)));
        return Convert.ToHexString(bytes)[..16];
    }

    private void PersistShelf() => _storage.SaveLibrary(_shelf);

    private void PersistProgress() => _storage.SaveProgress(_progress);

    private void PersistBookmarks() => _storage.SaveBookmarks(_bookmarks);

    private void PersistHighlights() => _storage.SaveHighlights(_highlights);
}
