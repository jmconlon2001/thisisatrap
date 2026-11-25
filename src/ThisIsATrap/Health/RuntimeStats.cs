namespace ThisIsATrap.Health;

/// <summary>Process-lifetime counters shown on the status page.</summary>
public sealed class RuntimeStats(TimeProvider time)
{
    private long _received;
    private long _rejected;
    private long _lastMessageTicks;

    private volatile string _smtpStatus = "starting";

    public DateTimeOffset StartedUtc { get; } = time.GetUtcNow();

    /// <summary>"starting", "listening on port N" or "error: …", as reported by the SMTP listener.</summary>
    public string SmtpStatus { get => _smtpStatus; set => _smtpStatus = value; }

    public bool SmtpListening => _smtpStatus.StartsWith("listening", StringComparison.Ordinal);
    public TimeSpan Uptime => time.GetUtcNow() - StartedUtc;

    public long MessagesReceived => Interlocked.Read(ref _received);
    public long MessagesRejected => Interlocked.Read(ref _rejected);

    public DateTimeOffset? LastMessageUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastMessageTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void MessageReceived()
    {
        Interlocked.Increment(ref _received);
        Interlocked.Exchange(ref _lastMessageTicks, time.GetUtcNow().UtcTicks);
    }

    public void MessageRejected() => Interlocked.Increment(ref _rejected);
}
