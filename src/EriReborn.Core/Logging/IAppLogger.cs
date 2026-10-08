namespace EriReborn.Core.Logging;

/// <summary>Module-scoped logger handed to services.</summary>
public interface IAppLogger
{
    string Module { get; }

    /// <summary>Derives a child logger whose module name is nested under this one.</summary>
    IAppLogger For(string module);

    void Write(LogLevel level, string operation, string message, Exception? exception = null);

    void Trace(string operation, string message) => Write(LogLevel.Trace, operation, message);

    void Debug(string operation, string message) => Write(LogLevel.Debug, operation, message);

    void Info(string operation, string message) => Write(LogLevel.Info, operation, message);

    void Warn(string operation, string message) => Write(LogLevel.Warn, operation, message);

    void Error(string operation, string message, Exception? exception = null) =>
        Write(LogLevel.Error, operation, message, exception);

    void Fatal(string operation, string message, Exception? exception = null) =>
        Write(LogLevel.Fatal, operation, message, exception);
}
