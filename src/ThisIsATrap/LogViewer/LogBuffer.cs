namespace ThisIsATrap.Logs;

public sealed record LogEntry(long Seq, DateTimeOffset Utc, string Level, string Category, string Message, string? Exception);

public sealed record LogPage(IReadOnlyList<LogEntry> Entries, long LastSeq, int Capacity, int Buffered, long Dropped);

/// <summary>
/// The most recent log entries, kept in memory for the Logs page. Nothing is written to disk;
/// the oldest entries are dropped once <see cref="Capacity"/> is reached.
/// </summary>
public sealed class LogBuffer(int capacity = 2000)
{
    public static readonly string[] Levels = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    private readonly LinkedList<LogEntry> _entries = new();
    private readonly Lock _lock = new();
    private long _seq;
    private long _dropped;

    public int Capacity { get; } = Math.Clamp(capacity, 100, 100_000);

    public void Add(DateTimeOffset utc, LogLevel level, string category, string message, Exception? exception)
    {
        lock (_lock)
        {
            _entries.AddLast(new LogEntry(++_seq, utc, level.ToString(), category, message, exception?.ToString()));
            while (_entries.Count > Capacity) { _entries.RemoveFirst(); _dropped++; }
        }
    }

    /// <summary>
    /// Entries after <paramref name="after"/> (for live tailing), at or above <paramref name="minLevel"/>,
    /// whose category or text contains <paramref name="search"/>. Newest last; at most <paramref name="take"/> (the newest ones).
    /// </summary>
    public LogPage Query(long after = 0, string? minLevel = null, string? search = null, string? category = null, int take = 500)
    {
        var min = Array.IndexOf(Levels, minLevel ?? "");
        List<LogEntry> matches;
        long last;
        int buffered;
        long dropped;
        lock (_lock)
        {
            matches = _entries.Where(e => e.Seq > after
                    && (min < 0 || Array.IndexOf(Levels, e.Level) >= min)
                    && (string.IsNullOrEmpty(category) || e.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrEmpty(search)
                        || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || e.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || (e.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
                .ToList();
            last = _seq;
            buffered = _entries.Count;
            dropped = _dropped;
        }
        take = Math.Clamp(take, 1, 5000);
        if (matches.Count > take) matches = matches[^take..];
        return new LogPage(matches, last, Capacity, buffered, dropped);
    }

    public int Clear()
    {
        lock (_lock)
        {
            var n = _entries.Count;
            _entries.Clear();
            return n;
        }
    }
}

/// <summary>Feeds every log message the app writes (after the normal level filters) into the <see cref="LogBuffer"/>.</summary>
public sealed class BufferLoggerProvider(LogBuffer buffer, TimeProvider? time = null) : ILoggerProvider
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, Shorten(categoryName), _time);

    public void Dispose() { }

    /// <summary>"ThisIsATrap.SmtpListenerService" → "SmtpListenerService"; framework categories keep a short prefix.</summary>
    public static string Shorten(string category)
    {
        if (category.StartsWith("ThisIsATrap.", StringComparison.Ordinal)) return category[(category.LastIndexOf('.') + 1)..];
        var parts = category.Split('.');
        return parts.Length <= 3 ? category : $"{parts[0]}.{parts[^1]}";
    }

    private sealed class BufferLogger(LogBuffer buffer, string category, TimeProvider time) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            buffer.Add(time.GetUtcNow(), logLevel, category, formatter(state, exception), exception);
        }
    }
}
