namespace EriReborn.Core.Logging;

/// <summary>Writes to stdout; used for headless smoke runs and diagnostics.</summary>
public sealed class ConsoleLogSink : ILogSink
{
    private readonly object _gate = new();

    public void Write(LogEntry entry)
    {
        lock (_gate)
        {
            Console.Out.WriteLine(entry.ToLine());
            Console.Out.Flush();
        }
    }
}
