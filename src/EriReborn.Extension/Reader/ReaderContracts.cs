namespace EriReborn.Extension.Reader;

/// <summary>
/// A book on the reader's shelf. The identity is a hash of the path, so the same
/// file opened twice is one book with one progress, not two shelves entries.
/// </summary>
public sealed record ReaderBookInfo(
    string Id,
    string Title,
    string FilePath,
    string Format,
    long SizeBytes,
    DateTimeOffset AddedAt);

/// <summary>
/// One navigable section of a book. Epub chapters come from the book's own TOC;
/// txt chapters are detected from heading lines, and a book with no detectable
/// structure simply has none — the page slider still works.
/// </summary>
public sealed record ReaderChapter(string Title, int StartPageIndex, int Level);

/// <summary>Where the reader left a book, so opening it again resumes.</summary>
public sealed record ReaderProgress(int PageIndex, int TotalPages, DateTimeOffset UpdatedAt);

/// <summary>
/// A user mark on one spot of one book. The quoted text is kept beside the page
/// index on purpose: re-paginating at a new font size moves pages, but the quote
/// still finds its place.
/// </summary>
public sealed record ReaderBookmark(
    string Id,
    int PageIndex,
    string? TextQuote,
    string? Note,
    DateTimeOffset CreatedAt);

/// <summary>
/// A highlighted span (划线), anchored to whole-book character offsets — the same
/// trick pagination uses — so it stays glued to its words across re-pagination and
/// font changes. The quote rides along so the annotation panel can show the mark
/// without opening its page.
/// </summary>
public sealed record ReaderHighlight(
    string Id,
    int Start,
    int End,
    string TextQuote,
    string? Note,
    DateTimeOffset CreatedAt);

/// <summary>One highlighted span as seen on a page; offsets are page-relative.</summary>
public sealed record ReaderPageHighlight(string Id, int Start, int End, string? Note);

/// <summary>
/// One full-text search hit. The match is anchored to its whole-book offset (the
/// same anchor pagination and highlights use), carries the page it lands on today,
/// and is already split into prefix/match/suffix so the panel can bold the hit
/// without any UI-side text slicing.
/// </summary>
public sealed record ReaderSearchMatch(
    int Index,
    int Offset,
    int Length,
    int PageIndex,
    string Prefix,
    string Match,
    string Suffix);

/// <summary>How the reading surface looks. Persisted across books.</summary>
public sealed record ReaderSettings(
    double FontSize,
    double LineHeight,
    string? FontFamily,
    string Theme)
{
    public static ReaderSettings Default { get; } = new(17, 1.7, null, "paper");
}

/// <summary>What the window renders for one page, right now.</summary>
public sealed record ReaderPageContent(
    int PageIndex,
    int TotalPages,
    string Text,
    string BookTitle,
    IReadOnlyList<ReaderBookmark> BookmarksOnThisPage,
    IReadOnlyList<ReaderPageHighlight> HighlightsOnThisPage,
    int DisplayStart);

/// <summary>
/// Everything a reader window needs, implemented by the extension and consumed by
/// the host's UI. This is the seam that keeps the extension free of any UI toolkit:
/// the window asks this interface for pages, chapters and settings, and never
/// learns how the bytes were read.
/// </summary>
public interface IReaderSession
{
    /// <summary>The books the user has opened before.</summary>
    IReadOnlyList<ReaderBookInfo> Bookshelf { get; }

    event EventHandler? BookshelfChanged;

    /// <summary>Opens the file picker and loads the chosen book, adding it to the shelf.</summary>
    Task<ReaderBookInfo?> OpenFromPickerAsync(CancellationToken ct = default);

    /// <summary>Reopens a book from the shelf and resumes its saved progress.</summary>
    Task<ReaderBookInfo?> OpenFromBookshelfAsync(string bookId, CancellationToken ct = default);

    /// <summary>Takes a book off the shelf. The file on disk is not touched.</summary>
    Task RemoveFromBookshelfAsync(string bookId);

    ReaderBookInfo? CurrentBook { get; }

    IReadOnlyList<ReaderChapter> Chapters { get; }

    /// <summary>The current book's marks, newest last. Empty when no book is open.</summary>
    IReadOnlyList<ReaderBookmark> Bookmarks { get; }

    /// <summary>The current book's highlights, in reading order. Empty when no book is open.</summary>
    IReadOnlyList<ReaderHighlight> Highlights { get; }

    ReaderSettings Settings { get; }

    event EventHandler? CurrentBookChanged;

    event EventHandler? SettingsChanged;

    /// <summary>Raised when the current book's marks change (added, removed, book switched).</summary>
    event EventHandler? BookmarksChanged;

    /// <summary>Raised when the current book's highlights change (added, removed).</summary>
    event EventHandler? HighlightsChanged;

    /// <summary>
    /// The content of one page. Pages are re-derived from the current settings, so a
    /// font-size change reshuffles indices and the caller re-asks for its page.
    /// </summary>
    Task<ReaderPageContent?> GetPageAsync(int pageIndex, CancellationToken ct = default);

    int TotalPages { get; }

    int CurrentPageIndex { get; }

    Task SaveProgressAsync(int pageIndex);

    Task<ReaderBookmark> AddBookmarkAsync(int pageIndex, string? quote, string? note);

    Task RemoveBookmarkAsync(string bookmarkId);

    /// <summary>
    /// Highlights the selected span of one page's text, given in page-relative
    /// indices (what a text selection reports), optionally with a note. The span is
    /// stored as whole-book offsets, so it survives re-pagination.
    /// </summary>
    Task<ReaderHighlight> AddHighlightAsync(int pageIndex, int relStart, int relEnd, string? note);

    Task RemoveHighlightAsync(string highlightId);

    /// <summary>The page that contains a whole-book character offset today.</summary>
    int PageIndexForOffset(int offset);

    /// <summary>
    /// Finds every occurrence of <paramref name="query"/> in the current book, in
    /// reading order with a capped result count. Case-insensitive, offset-anchored;
    /// empty when no book is open or the query is blank.
    /// </summary>
    Task<IReadOnlyList<ReaderSearchMatch>> SearchAsync(string query, CancellationToken ct = default);

    Task UpdateSettingsAsync(ReaderSettings next);
}
