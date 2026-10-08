namespace EriReborn.Core.Logging;

/// <summary>
/// Process-wide logging facade. Sinks are registered by the composition root
/// (spec 67); nothing else configures logging.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static readonly List<ILogSink> Sinks = new();
    private static LogLevel _minimum = LogLevel.Info;

    public static LogLevel MinimumLevel
    {
        get => _minimum;
        set => _minimum = value;
    }

    public static void AddSink(ILogSink sink)
    {
        lock (Gate)
        {
            Sinks.Add(sink);
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            Sinks.Clear();
        }
    }

    public static IAppLogger For(string module) => new AppLogger(module);

    public static IAppLogger For<T>() => new AppLogger(typeof(T).Name);

    internal static void Emit(LogEntry entry)
    {
        if (entry.Level < _minimum)
        {
            return;
        }

        ILogSink[] snapshot;
        lock (Gate)
        {
            snapshot = Sinks.ToArray();
        }

        foreach (var sink in snapshot)
        {
            try
            {
                sink.Write(entry);
            }
            catch
            {
                // Never let a sink failure escape into application flow.
            }
        }
    }

    private sealed class AppLogger(string module) : IAppLogger
    {
        public string Module { get; } = module;

        public IAppLogger For(string childModule) => new AppLogger(Module + "." + childModule);

        public void Write(LogLevel level, string operation, string message, Exception? exception = null)
        {
            Emit(new LogEntry(
                DateTimeOffset.Now,
                level,
                Module,
                operation,
                message,
                exception?.GetType().FullName,
                exception?.Message,
                Describe(exception)));
        }

        /// <summary>
        /// The exception as it is normally printed: the type, the message, and every inner exception with its
        /// own stack.
        ///
        /// <para>
        /// This used to record <c>exception.StackTrace</c> alone, and that made a whole class of crash
        /// unreadable. A <see cref="TypeInitializationException"/> says only "the type initializer threw"; the
        /// exception that actually threw — the one naming the field, the file, the value — lives in
        /// <c>InnerException</c>, which was dropped on the floor. The crash at 19:41 today in this very log is
        /// the example: the real cause never reached the file, so the failure had to be traced by reading the
        /// code instead. A crash log nobody can read is the same as no crash log (spec 58/69).
        /// </para>
        /// </summary>
        private static string? Describe(Exception? exception) => exception?.ToString();
    }
}
