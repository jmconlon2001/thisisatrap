namespace ThisIsATrap;

/// <summary>Periodically removes old messages according to the user-editable purge rules.</summary>
public sealed class PurgeService(MailStore store, EventHub events, ILogger<PurgeService> log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);

    public static DateTimeOffset? LastRunUtc { get; private set; }
    public static int LastRemoved { get; private set; }

    /// <summary>Re-evaluate now (called after settings change so a shorter interval takes effect immediately).</summary>
    public void Wake() => _wake.Release();

    public int RunOnce()
    {
        var removed = store.Purge(store.GetPurgeSettings() with { Enabled = true });
        Record(removed);
        return removed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = store.GetPurgeSettings();
            if (settings.Enabled)
            {
                try { Record(store.Purge(settings)); }
                catch (Exception ex) { log.LogError(ex, "Auto-purge failed"); }
            }

            var interval = TimeSpan.FromMinutes(Math.Clamp(settings.IntervalMinutes, 1, 24 * 60));
            try { await _wake.WaitAsync(interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
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
