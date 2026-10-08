using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EriReborn.Core.Logging;
using EriReborn.Extension.Reader;

namespace EriReborn.App.Shared.ViewModels;

/// <summary>One annotation as the panel shows it: the highlight plus the page it lands on today.</summary>
public sealed record ReaderHighlightItem(ReaderHighlight Highlight, int Page)
{
    public string Quote => Highlight.TextQuote;

    public string? Note => Highlight.Note;

    public bool HasNote => Note is not null;
}

/// <summary>One search hit as the panel shows it; the 1-based page is resolved for display.</summary>
public sealed record ReaderSearchResultItem(ReaderSearchMatch Match, int Page)
{
    public int Offset => Match.Offset;

    public int Length => Match.Length;

    public string Prefix => Match.Prefix;

    public string Word => Match.Match;

    public string Suffix => Match.Suffix;
}

/// <summary>
/// The reader window's view model. All reading logic lives in the extension's
/// <see cref="IReaderSession"/>; this class only mirrors that state into bindable
/// properties and turns commands into session calls, so the window stays dumb and
/// the session keeps working even when no window exists.
/// </summary>
public partial class ReaderWindowViewModel : ObservableObject
{
    private readonly IReaderSession _session;
    private readonly IAppLogger _log;

    /// <summary>Where the view model was created; session events are marshalled here.</summary>
    private readonly SynchronizationContext? _context;

    public ReaderWindowViewModel(IReaderSession session, IAppLogger log)
    {
        _session = session;
        _log = log;
        _context = SynchronizationContext.Current;

        _fontSize = session.Settings.FontSize;
        _lineHeight = session.Settings.LineHeight;
        _theme = session.Settings.Theme;

        _session.BookshelfChanged += (_, _) => OnUi(() => Bookshelf = _session.Bookshelf);
        _session.CurrentBookChanged += (_, _) => OnUi(() => _ = RefreshCurrentPageAsync());
        _session.BookmarksChanged += (_, _) => OnUi(() => Bookmarks = _session.Bookmarks);
        _session.HighlightsChanged += (_, _) => OnUi(RefreshHighlights);

        Bookshelf = _session.Bookshelf;
        Bookmarks = _session.Bookmarks;

        if (_session.CurrentBook is not null)
        {
            _ = RefreshCurrentPageAsync();
        }
    }

    [ObservableProperty]
    private IReadOnlyList<ReaderBookInfo> _bookshelf;

    [ObservableProperty]
    private ReaderPageContent? _currentPage;

    [ObservableProperty]
    private IReadOnlyList<ReaderChapter> _chapters = Array.Empty<ReaderChapter>();

    [ObservableProperty]
    private IReadOnlyList<ReaderBookmark> _bookmarks = Array.Empty<ReaderBookmark>();

    [ObservableProperty]
    private IReadOnlyList<ReaderHighlightItem> _highlights = Array.Empty<ReaderHighlightItem>();

    [ObservableProperty]
    private bool _showHighlightsPanel;

    [ObservableProperty]
    private bool _showSearchPanel;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<ReaderSearchResultItem> _searchResults = Array.Empty<ReaderSearchResultItem>();

    [ObservableProperty]
    private bool _isSearching;

    /// <summary>搜索命中在当前页文本中的强调区间；翻页或打开别的书即清掉。</summary>
    [ObservableProperty]
    private int? _searchEmphasisStart;

    [ObservableProperty]
    private int? _searchEmphasisEnd;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private double _lineHeight;

    [ObservableProperty]
    private string _theme;

    [ObservableProperty]
    private bool _showBookshelf;

    [ObservableProperty]
    private bool _showToc;

    [ObservableProperty]
    private bool _showBookmarksPanel;

    [ObservableProperty]
    private string _status = "没有打开的书。";

    /// <summary>Chapters already on or before the current page, for the status bar.</summary>
    public string CurrentChapter
    {
        get
        {
            if (CurrentPage is null || Chapters.Count == 0)
            {
                return string.Empty;
            }

            var page = CurrentPage.PageIndex;
            var current = Chapters.LastOrDefault(chapter => chapter.StartPageIndex <= page);
            return current?.Title ?? string.Empty;
        }
    }

    public string ProgressText
        => CurrentPage is { } page
            ? $"第 {page.PageIndex + 1} / {page.TotalPages} 页（{(page.TotalPages == 0 ? 0 : (page.PageIndex + 1) * 100 / page.TotalPages)}%）"
            : "—";

    public bool HasBook => CurrentPage is not null;

    public double FontMin => 12;

    public double FontMax => 28;

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        Status = "正在打开…";
        var book = await _session.OpenFromPickerAsync().ConfigureAwait(true);
        if (book is null)
        {
            Status = "没有选择文件。";
        }
    }

    [RelayCommand]
    private async Task OpenBookAsync(string? bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            return;
        }

        Status = "正在打开…";
        var book = await _session.OpenFromBookshelfAsync(bookId).ConfigureAwait(true);
        if (book is null)
        {
            Status = "这本书的文件已不在磁盘上。";
        }
    }

    [RelayCommand]
    private async Task RemoveBookAsync(string? bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            return;
        }

        await _session.RemoveFromBookshelfAsync(bookId).ConfigureAwait(true);
        Status = "已从书架移除（文件本身未删除）。";
    }

    [RelayCommand]
    private Task NextPageAsync() => GoToAsync(_session.CurrentPageIndex + 1);

    [RelayCommand]
    private Task PrevPageAsync() => GoToAsync(_session.CurrentPageIndex - 1);

    [RelayCommand]
    private Task FirstPageAsync() => GoToAsync(0);

    [RelayCommand]
    private Task LastPageAsync() => GoToAsync(_session.TotalPages - 1);

    [RelayCommand]
    private Task GoToChapterAsync(object? parameter)
    {
        return parameter is ReaderChapter chapter ? GoToAsync(chapter.StartPageIndex) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task GoToBookmarkAsync(object? parameter)
    {
        return parameter is ReaderBookmark mark ? GoToAsync(mark.PageIndex) : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        if (CurrentPage is null)
        {
            return;
        }

        // A short quote keeps the mark findable even after a re-pagination moves
        // page indices; the session stores it beside the page number for that reason.
        var text = CurrentPage.Text;
        var quote = text.Length > 60 ? text[..60] : text;
        await _session.AddBookmarkAsync(CurrentPage.PageIndex, quote.Trim(), null).ConfigureAwait(true);
        Status = "已加书签。";
    }

    [RelayCommand]
    private async Task RemoveBookmarkAsync(string? bookmarkId)
    {
        if (string.IsNullOrWhiteSpace(bookmarkId))
        {
            return;
        }

        await _session.RemoveBookmarkAsync(bookmarkId).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ToggleHighlightsPanel()
    {
        ShowHighlightsPanel = !ShowHighlightsPanel;
        if (ShowHighlightsPanel)
        {
            RefreshHighlights();
        }
    }

    /// <summary>
    /// Highlights the reader's current text selection, given in page-relative
    /// indices, optionally with a note. Called by the window, which owns the
    /// selection gesture.
    /// </summary>
    public async Task AddHighlightFromSelectionAsync(int relStart, int relEnd, string? note)
    {
        if (CurrentPage is null)
        {
            return;
        }

        try
        {
            await _session.AddHighlightAsync(CurrentPage.PageIndex, relStart, relEnd, note).ConfigureAwait(true);
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
            return;
        }

        await RefreshCurrentPageAsync().ConfigureAwait(true);
        Status = "已划线。";
    }

    [RelayCommand]
    private async Task RemoveHighlightAsync(string? highlightId)
    {
        if (string.IsNullOrWhiteSpace(highlightId))
        {
            return;
        }

        await _session.RemoveHighlightAsync(highlightId).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task GoToHighlightAsync(object? parameter)
    {
        return parameter is ReaderHighlightItem item
            ? GoToAsync(_session.PageIndexForOffset(item.Highlight.Start))
            : Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleSearchPanel() => ShowSearchPanel = !ShowSearchPanel;

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (CurrentPage is null)
        {
            Status = "请先打开一本书再搜索。";
            return;
        }

        var query = SearchQuery?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            SearchResults = Array.Empty<ReaderSearchResultItem>();
            return;
        }

        IsSearching = true;
        IReadOnlyList<ReaderSearchMatch> matches;
        try
        {
            matches = await _session.SearchAsync(query).ConfigureAwait(true);
        }
        finally
        {
            IsSearching = false;
        }

        SearchResults = matches
            .Select(match => new ReaderSearchResultItem(match, match.PageIndex + 1))
            .ToList();

        Status = SearchResults.Count == 0
            ? "书中没有找到匹配内容。"
            : SearchResults.Count >= 1000
                ? "找到 1000+ 条匹配（仅显示前 1000 条），换个更具体的词试试。"
                : $"找到 {SearchResults.Count} 条匹配。";
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        SearchResults = Array.Empty<ReaderSearchResultItem>();
        SearchEmphasisStart = null;
        SearchEmphasisEnd = null;
    }

    [RelayCommand]
    private Task GoToSearchResultAsync(object? parameter)
    {
        return parameter is ReaderSearchResultItem item
            ? GoToAsync(item.Match.PageIndex, item.Match)
            : Task.CompletedTask;
    }

    [RelayCommand]
    private async Task IncreaseFontAsync() => await AdjustFontAsync(+1.5).ConfigureAwait(true);

    [RelayCommand]
    private async Task DecreaseFontAsync() => await AdjustFontAsync(-1.5).ConfigureAwait(true);

    [RelayCommand]
    private async Task IncreaseLineHeightAsync() => await AdjustLineHeightAsync(+0.1).ConfigureAwait(true);

    [RelayCommand]
    private async Task DecreaseLineHeightAsync() => await AdjustLineHeightAsync(-0.1).ConfigureAwait(true);

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        Theme = Theme switch
        {
            "paper" => "eye",
            "eye" => "night",
            _ => "paper",
        };
        await ApplySettingsAsync().ConfigureAwait(true);
        Status = $"主题：{Theme switch { "paper" => "纸墨", "eye" => "护眼", _ => "夜间" }}。";
    }

    [RelayCommand]
    private void ToggleBookshelf() => ShowBookshelf = !ShowBookshelf;

    [RelayCommand]
    private void ToggleToc()
    {
        ShowToc = !ShowToc;
        if (ShowToc)
        {
            Chapters = _session.Chapters;
        }
    }

    [RelayCommand]
    private void ToggleBookmarksPanel()
    {
        ShowBookmarksPanel = !ShowBookmarksPanel;
        if (ShowBookmarksPanel)
        {
            Bookmarks = _session.Bookmarks;
        }
    }

    private async Task AdjustFontAsync(double delta)
    {
        var next = Math.Clamp(FontSize + delta, FontMin, FontMax);
        if (Math.Abs(next - FontSize) < 0.01)
        {
            return;
        }

        FontSize = next;
        await ApplySettingsAsync().ConfigureAwait(true);
        Status = $"字号 {FontSize:0.#}。";
    }

    private async Task AdjustLineHeightAsync(double delta)
    {
        var next = Math.Clamp(LineHeight + delta, 1.0, 2.4);
        if (Math.Abs(next - LineHeight) < 0.01)
        {
            return;
        }

        LineHeight = Math.Round(next, 1);
        await ApplySettingsAsync().ConfigureAwait(true);
        Status = $"行距 {LineHeight:0.0}。";
    }

    private async Task ApplySettingsAsync()
    {
        await _session.UpdateSettingsAsync(new ReaderSettings(FontSize, LineHeight, null, Theme))
            .ConfigureAwait(true);
        Chapters = _session.Chapters;
        await RefreshCurrentPageAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Loads one page from the session and persists the position. Out-of-range
    /// indices clamp, so Next/Prev can be pressed without counting first. A search
    /// hit rides along as <paramref name="match"/> so the jumped-to page can
    /// emphasize it; any ordinary navigation clears the previous emphasis.
    /// </summary>
    private async Task GoToAsync(int pageIndex, ReaderSearchMatch? match = null)
    {
        if (_session.TotalPages == 0)
        {
            return;
        }

        var clamped = Math.Clamp(pageIndex, 0, _session.TotalPages - 1);
        var page = await _session.GetPageAsync(clamped).ConfigureAwait(true);
        if (page is null)
        {
            return;
        }

        await _session.SaveProgressAsync(page.PageIndex).ConfigureAwait(true);
        CurrentPage = page;

        if (match is not null)
        {
            var relStart = Math.Clamp(match.Offset - page.DisplayStart, 0, page.Text.Length);
            SearchEmphasisStart = relStart;
            SearchEmphasisEnd = Math.Clamp(relStart + match.Length, 0, page.Text.Length);
        }
        else
        {
            SearchEmphasisStart = null;
            SearchEmphasisEnd = null;
        }

        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CurrentChapter));
        OnPropertyChanged(nameof(HasBook));
        Status = string.Empty;
    }

    private async Task RefreshCurrentPageAsync()
    {
        Chapters = _session.Chapters;
        Bookmarks = _session.Bookmarks;
        Bookshelf = _session.Bookshelf;
        RefreshHighlights();

        // 换书或重新分页后，旧书的搜索结果和强调区间都不再有效。
        SearchResults = Array.Empty<ReaderSearchResultItem>();
        SearchEmphasisStart = null;
        SearchEmphasisEnd = null;

        await GoToAsync(_session.CurrentPageIndex).ConfigureAwait(true);
    }

    /// <summary>Mirrors the session's highlights with their pages resolved for display.</summary>
    private void RefreshHighlights()
    {
        Highlights = _session.Highlights
            .Select(span => new ReaderHighlightItem(span, _session.PageIndexForOffset(span.Start) + 1))
            .ToList();
    }

    /// <summary>Runs the action on the thread the view model was built on.</summary>
    private void OnUi(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
