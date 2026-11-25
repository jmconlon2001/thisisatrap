namespace ThisIsATrap;

/// <summary>
/// Periodically removes old messages. Each inbox uses its own purge rules when it has them,
/// otherwise the global rules. The check interval is always the global one.
/// </summary>
public sealed class PurgeService(MailStore store, EventHub events, ILogger<PurgeService> log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);

    public static DateTimeOffset? LastRunUtc { get; private set; }
    public static int LastRemoved { get; private set; }

    /// <summary>Re-evaluate now (called after settings change so a shorter interval takes effect immediately).</summary>
    public void Wake() => _wake.Release();

    /// <summary>"Run purge now": applies every inbox's rules even when automatic purging is switched off.</summary>
    public int RunOnce()
    {
        var removed = PurgeAll(force: true);
        Record(removed);
        return removed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var global = store.GetPurgeSettings();
            try { Record(PurgeAll(force: false)); }
            catch (Exception ex) { log.LogError(ex, "Auto-purge failed"); }

            var interval = TimeSpan.FromMinutes(Math.Clamp(global.IntervalMinutes, 1, 24 * 60));
            try { await _wake.WaitAsync(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private int PurgeAll(bool force)
    {
        var global = store.GetPurgeSettings();
        var removed = 0;
        foreach (var inbox in store.ListInboxes())
        {
            var rules = inbox.Purge ?? global;
            if (rules.Enabled || force) removed += store.Purge(rules, inbox.Id);
        }
        return removed;
    }

    private void Record(int removed)
    {
        LastRunUtc = DateTimeOffset.UtcNow;
        LastRemoved = removed;
        if (removed > 0)
        {
            log.LogInformation("Purged {Count} message(s)", removed);
            events.Publish("purged", new { removed });
        }
    }
}
