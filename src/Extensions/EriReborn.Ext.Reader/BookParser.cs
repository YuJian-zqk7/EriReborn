using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using EriReborn.Extension.Reader;

namespace EriReborn.Ext.Reader;

/// <summary>A chapter heading found in the raw text, located by character offset.</summary>
internal sealed record DetectedChapter(int Offset, string Title);

/// <summary>The parsed body of one book: full text plus where its sections start.</summary>
internal sealed record ParsedBook(string FullText, IReadOnlyList<DetectedChapter> Chapters);

/// <summary>One page as a slice of the full text, so re-pagination keeps positions.</summary>
internal sealed record PageSlice(int Start, int End)
{
    public string Slice(string text) => text[Start..End].Trim();

    /// <summary>
    /// The page text together with the whole-book offset of its first displayed
    /// character. Trim() shifts that start, and highlights are anchored to
    /// whole-book offsets, so the renderer and the selection converter must agree
    /// on this trimmed origin — one helper keeps the two from drifting apart.
    /// </summary>
    public (int DisplayStart, string Text) DisplaySlice(string text)
    {
        var raw = text[Start..End];
        var leading = raw.Length - raw.TrimStart().Length;
        return (Start + leading, raw.Trim());
    }
}

/// <summary>
/// Turns a book file into text the reader can page through. The parsing here is the
/// same that powered the inline page; what grew on top is structure — chapter
/// detection and offset-based pagination — so the window can offer a TOC and keep
/// the reading position across font-size changes.
/// </summary>
internal static class BookParser
{
    /// <summary>
    /// Heading lines a plain txt novel uses. Chinese chapter words cover the vast
    /// majority of real files; the English form and the fixed prologue/epilogue words
    /// catch the rest. A file that matches nothing simply has no TOC.
    /// </summary>
    private static readonly Regex TxtHeading = new(
        @"^[ \t　]*(?:第[一二三四五六七八九十百千万零〇两0-9]+[章节卷回部集篇][ \t　]*\S.{0,40}|(?:序章|楔子|引子|前言|后记|尾声|番外)\S{0,20}|Chapter\s+\d+.{0,40})[ \t　]*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static ParsedBook ParseTxt(string path, Action<string, string> log)
    {
        var text = DecodeText(path, log);
        var chapters = new List<DetectedChapter>();
        foreach (Match match in TxtHeading.Matches(text))
        {
            var title = match.Value.Trim().TrimEnd('　', ' ', '\t');
            if (title.Length > 40)
            {
                title = title[..40];
            }

            chapters.Add(new DetectedChapter(match.Index, title));
        }

        log("info", $"阅读器：{Path.GetFileName(path)} 解析完成（{text.Length} 字符，识别到 {chapters.Count} 个章节标题）。");
        return new ParsedBook(text, chapters);
    }

    public static ParsedBook ParseEpub(string path, Action<string, string> log)
    {
        using var archive = ZipFile.OpenRead(path);

        var opfPath = FindOpfPath(archive);
        if (opfPath is null)
        {
            log("warn", $"阅读器：{Path.GetFileName(path)} 里找不到 OPF，按空书处理。");
            return new ParsedBook(string.Empty, Array.Empty<DetectedChapter>());
        }

        var opf = ReadEntryText(archive, opfPath);
        var spineIds = ExtractSpineIdrefs(opf);
        var idToHref = ExtractManifestIdHref(opf);
        var opfDir = Path.GetDirectoryName(opfPath.Replace('\\', '/')) ?? string.Empty;

        var builder = new StringBuilder();
        var chapters = new List<DetectedChapter>();
        var written = 0;

        foreach (var idref in spineIds)
        {
            if (!idToHref.TryGetValue(idref, out var href))
            {
                continue;
            }

            var entryPath = string.IsNullOrEmpty(opfDir) ? href : opfDir + "/" + href;
            var xhtml = ReadEntryText(archive, entryPath);
            var doc = HtmlToPlainText(xhtml);
            if (doc.Length == 0)
            {
                continue;
            }

            // A spine document is one TOC entry; its title is the document's own
            // <title>, falling back to the first heading, falling back to its file name.
            var title = ExtractDocTitle(xhtml) ?? Path.GetFileNameWithoutExtension(href);
            chapters.Add(new DetectedChapter(written, title));

            builder.Append(doc);
            builder.Append("\n\n");
            written = builder.Length;
        }

        var text = builder.ToString().Trim();
        log("info", $"阅读器：{Path.GetFileName(path)} 解析完成（{text.Length} 字符，{chapters.Count} 个内容文档）。");
        return new ParsedBook(text, chapters);
    }

    /// <summary>
    /// Cuts the text into slices of about <paramref name="pageChars"/>, preferring a
    /// line break near the end over a mid-sentence cut. Slices keep their offsets so
    /// a re-pagination can find the old position again.
    /// </summary>
    public static List<PageSlice> Paginate(string text, int pageChars)
    {
        var slices = new List<PageSlice>();
        if (text.Length == 0)
        {
            return slices;
        }

        for (var start = 0; start < text.Length; )
        {
            var end = Math.Min(start + pageChars, text.Length);
            if (end < text.Length)
            {
                var searchFrom = Math.Max(start + pageChars - 2_000, start + 1);
                var breakAt = text.LastIndexOf('\n', end - 1, end - searchFrom);
                if (breakAt > start)
                {
                    end = breakAt + 1;
                }
            }

            slices.Add(new PageSlice(start, end));
            start = end;
        }

        return slices;
    }

    /// <summary>
    /// Maps detected chapter offsets onto the pages they live on, so a TOC entry is a
    /// page number the window can jump to.
    /// </summary>
    public static List<ReaderChapter> BuildChapters(ParsedBook book, IReadOnlyList<PageSlice> pages)
    {
        var result = new List<ReaderChapter>();
        if (pages.Count == 0)
        {
            return result;
        }

        foreach (var detected in book.Chapters)
        {
            // Binary search for the slice that contains this offset.
            var low = 0;
            var high = pages.Count - 1;
            var pageIndex = 0;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (pages[mid].Start <= detected.Offset)
                {
                    pageIndex = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            result.Add(new ReaderChapter(detected.Title, pageIndex, 0));
        }

        // Consecutive spine documents can share a page at page sizes this coarse; a
        // TOC with two entries pointing at the same page reads as broken, so only the
        // first of each duplicate position is kept.
        for (var i = result.Count - 2; i >= 0; i--)
        {
            if (result[i + 1].StartPageIndex == result[i].StartPageIndex)
            {
                result.RemoveAt(i + 1);
            }
        }

        return result;
    }

    /// <summary>
    /// How many characters one page holds at these settings. The figure is an
    /// approximation — real layout depends on glyphs and window size — but it moves
    /// the right way: bigger type, fewer characters per page.
    /// </summary>
    public static int PageCharBudget(EriReborn.Extension.Reader.ReaderSettings settings)
    {
        var baseSize = ReaderSettings.Default.FontSize;
        var baseHeight = ReaderSettings.Default.LineHeight;
        var scale = (baseSize / Math.Max(8, settings.FontSize)) * (baseHeight / Math.Max(1.0, settings.LineHeight));
        return Math.Clamp((int)(12_000 * scale), 2_000, 80_000);
    }

    private static string? ExtractDocTitle(string xhtml)
    {
        var title = Regex.Match(xhtml, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (title.Success)
        {
            var text = System.Net.WebUtility.HtmlDecode(title.Groups[1].Value).Trim();
            if (text.Length > 0)
            {
                return text.Length > 60 ? text[..60] : text;
            }
        }

        var heading = Regex.Match(xhtml, @"<h[1-3][^>]*>(.*?)</h[1-3]>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (heading.Success)
        {
            var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(heading.Groups[1].Value, "<[^>]+>", string.Empty)).Trim();
            if (text.Length > 0)
            {
                return text.Length > 60 ? text[..60] : text;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a text file. Byte-order marks decide first (UTF-8 / UTF-16); without a
    /// BOM, strict UTF-8 is tried, and only on failure do the East Asian code pages
    /// get asked for explicitly — that is where GBK novels are decoded.
    /// </summary>
    /// <remarks>
    /// Two traps that both produced pure replacement-mark pages before:
    /// Encoding.Default in .NET 8 is UTF-8 (not the system ANSI page), and the app
    /// process opts into <c>activeCodePage=UTF-8</c> in its manifest anyway, so
    /// CP_ACP cannot be trusted either. The candidate code pages are validated
    /// strictly (MB_ERR_INVALID_CHARS) and the first one that decodes cleanly wins.
    /// </remarks>
    private static string DecodeText(string path, Action<string, string> log)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            log("info", $"阅读器：{Path.GetFileName(path)} 带 UTF-8 BOM。");
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            log("info", $"阅读器：{Path.GetFileName(path)} 带 UTF-16LE BOM。");
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            log("info", $"阅读器：{Path.GetFileName(path)} 带 UTF-16BE BOM。");
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            var utf8 = new UTF8Encoding(false, true).GetString(bytes);
            log("info", $"阅读器：{Path.GetFileName(path)} 按 UTF-8 解码（{utf8.Length} 字符）。");
            return utf8;
        }
        catch (DecoderFallbackException)
        {
            // 936 GBK 简体（中文小说最常见）、950 Big5 繁体、932 Shift-JIS、949 EUC-KR。
            foreach (var codePage in new uint[] { 936, 950, 932, 949 })
            {
                var text = TryDecodeStrict(codePage, bytes);
                if (text is not null)
                {
                    log("info", $"阅读器：{Path.GetFileName(path)} 按代码页 {codePage} 解码（{text.Length} 字符）。");
                    return text;
                }
            }

            log("warn", $"阅读器：{Path.GetFileName(path)} 无法识别编码，按 UTF-8 宽松解码（可能出现乱码）。");
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private const uint MbErrInvalidChars = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] source, int sourceLength, [Out] char[]? destination, int destinationLength);

    /// <summary>Decodes with an explicit code page; null when the bytes are invalid for it.</summary>
    private static string? TryDecodeStrict(uint codePage, byte[] bytes)
    {
        var length = MultiByteToWideChar(codePage, MbErrInvalidChars, bytes, bytes.Length, null, 0);
        if (length <= 0)
        {
            return null;
        }

        var chars = new char[length];
        if (MultiByteToWideChar(codePage, MbErrInvalidChars, bytes, bytes.Length, chars, length) <= 0)
        {
            return null;
        }

        return new string(chars);
    }

    private static string? FindOpfPath(ZipArchive archive)
    {
        var container = archive.GetEntry("META-INF/container.xml");
        if (container is null)
        {
            return null;
        }

        using var stream = container.Open();
        using var reader = new StreamReader(stream);
        var xml = reader.ReadToEnd();
        var match = Regex.Match(xml, @"full-path=""([^""]+)""");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static Dictionary<string, string> ExtractManifestIdHref(string opf)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(opf, @"<item[^>]*id=""([^""]+)""[^>]*href=""([^""]+)"""))
        {
            result[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return result;
    }

    private static List<string> ExtractSpineIdrefs(string opf)
    {
        var spineMatch = Regex.Match(opf, @"<spine[^>]*>(.*?)</spine>", RegexOptions.Singleline);
        if (!spineMatch.Success)
        {
            return new List<string>();
        }

        var ids = new List<string>();
        foreach (Match m in Regex.Matches(spineMatch.Groups[1].Value, @"idref=""([^""]+)"""))
        {
            ids.Add(m.Groups[1].Value);
        }
        return ids;
    }

    private static string ReadEntryText(ZipArchive archive, string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/');
        var entry = archive.GetEntry(normalized);
        if (entry is null)
        {
            return string.Empty;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string HtmlToPlainText(string html)
    {
        // Block tags become line breaks; inline tags disappear. Entities are
        // decoded last so a stray &amp; inside an attribute does not survive.
        var text = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(br|p|div|h[1-6]|li|tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }
}
