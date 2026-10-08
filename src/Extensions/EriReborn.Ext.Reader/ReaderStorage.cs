using System.Text.Json;
using EriReborn.Extension.Reader;

namespace EriReborn.Ext.Reader;

/// <summary>
/// The reader's saved state, one JSON file per kind, written atomically (a temp
/// file then a replace) so a crash mid-write can never leave half a library behind.
/// A missing or corrupt file reads as empty rather than throwing — saved reading
/// state is a convenience, not data worth failing over.
/// </summary>
internal sealed class ReaderStorage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private readonly string? _directory;

    public ReaderStorage(string? directory)
    {
        _directory = directory;
    }

    public bool Available => _directory is not null;

    public List<ReaderBookInfo> LoadLibrary()
        => Load<List<ReaderBookInfo>>("library.json") ?? new List<ReaderBookInfo>();

    public void SaveLibrary(List<ReaderBookInfo> books)
        => Save("library.json", books);

    public Dictionary<string, ReaderProgress> LoadProgress()
        => Load<Dictionary<string, ReaderProgress>>("progress.json")
           ?? new Dictionary<string, ReaderProgress>(StringComparer.Ordinal);

    public void SaveProgress(Dictionary<string, ReaderProgress> progress)
        => Save("progress.json", progress);

    public Dictionary<string, List<ReaderBookmark>> LoadBookmarks()
        => Load<Dictionary<string, List<ReaderBookmark>>>("bookmarks.json")
           ?? new Dictionary<string, List<ReaderBookmark>>(StringComparer.Ordinal);

    public void SaveBookmarks(Dictionary<string, List<ReaderBookmark>> bookmarks)
        => Save("bookmarks.json", bookmarks);

    public Dictionary<string, List<ReaderHighlight>> LoadHighlights()
        => Load<Dictionary<string, List<ReaderHighlight>>>("highlights.json")
           ?? new Dictionary<string, List<ReaderHighlight>>(StringComparer.Ordinal);

    public void SaveHighlights(Dictionary<string, List<ReaderHighlight>> highlights)
        => Save("highlights.json", highlights);

    public ReaderSettings LoadSettings()
        => Load<ReaderSettings>("settings.json") ?? ReaderSettings.Default;

    public void SaveSettings(ReaderSettings settings)
        => Save("settings.json", settings);

    private string PathOf(string fileName) => Path.Combine(_directory!, fileName);

    private T? Load<T>(string fileName) where T : class
    {
        if (_directory is null)
        {
            return null;
        }

        try
        {
            var path = PathOf(fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception)
        {
            // Corrupt state reads as nothing saved. The reader still works; the
            // user just starts this one file over.
            return null;
        }
    }

    private void Save<T>(string fileName, T payload)
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathOf(fileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Losing a save is quieter than crashing the reader over one.
        }
    }
}
