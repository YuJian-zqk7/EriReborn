namespace EriReborn.Core.Logging;

/// <summary>A destination for structured log entries.</summary>
public interface ILogSink
{
    void Write(LogEntry entry);
}
