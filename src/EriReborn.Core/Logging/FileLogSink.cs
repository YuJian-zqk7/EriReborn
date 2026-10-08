namespace EriReborn.Core.Logging;

/// <summary>
/// Thread-safe append-only file sink. Startup failures must survive the
/// process, so every write is flushed (spec 66).
/// </summary>
public sealed class FileLogSink : ILogSink
{
    private readonly object _gate = new();
    private readonly string _path;

    public FileLogSink(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string FilePath => _path;

    public void Write(LogEntry entry)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(_path, entry.ToLine() + Environment.NewLine);
            }
        }
        catch
        {
            // A logging failure must never take the application down.
        }
    }
}
