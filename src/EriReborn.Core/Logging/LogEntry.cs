namespace EriReborn.Core.Logging;

/// <summary>
/// One structured log record. Spec 65 requires Timestamp, Level, Module,
/// Operation, Message, Exception and StackTrace on key logs.
/// </summary>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Module,
    string Operation,
    string Message,
    string? ExceptionType = null,
    string? ExceptionMessage = null,
    string? StackTrace = null)
{
    public string ToLine()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(" [").Append(Level.ToString().ToUpperInvariant()).Append(']');
        sb.Append(" [").Append(Module).Append(']');
        if (!string.IsNullOrEmpty(Operation))
        {
            sb.Append(" [").Append(Operation).Append(']');
        }

        sb.Append(' ').Append(Message);
        if (!string.IsNullOrEmpty(ExceptionType))
        {
            sb.Append(" | ").Append(ExceptionType).Append(": ").Append(ExceptionMessage);
        }

        if (!string.IsNullOrEmpty(StackTrace))
        {
            sb.Append(Environment.NewLine).Append(StackTrace);
        }

        return sb.ToString();
    }
}
