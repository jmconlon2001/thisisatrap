using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ThisIsATrap.Health;

/// <summary>Healthy while the SMTP listener is accepting connections.</summary>
public sealed class SmtpHealthCheck(RuntimeStats stats) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var s = stats.SmtpStatus;
        var data = new Dictionary<string, object> { ["status"] = s };
        return Task.FromResult(s.StartsWith("listening", StringComparison.Ordinal)
            ? HealthCheckResult.Healthy($"SMTP {s}", data)
            : HealthCheckResult.Unhealthy($"SMTP {s}", data: data));
    }
}

/// <summary>Healthy when the SQLite index answers a query.</summary>
public sealed class DatabaseHealthCheck(MailStore store) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = store.Stats();
            return Task.FromResult(HealthCheckResult.Healthy("Database reachable",
                new Dictionary<string, object> { ["messages"] = stats.Total }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Database query failed", ex));
        }
    }
}

/// <summary>
/// Unhealthy when the data folder can't be written; degraded when free disk space is low
/// (messages would soon start failing to save).
/// </summary>
public sealed class StorageHealthCheck(MailStore store, Func<string, (long Free, long Total)?>? disk = null) : IHealthCheck
{
    /// <summary>Below this much free space the check reports Degraded.</summary>
    public const long LowSpaceBytes = 512L * 1024 * 1024;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object> { ["dataPath"] = store.DataPath };
        try
        {
            var probe = Path.Combine(store.DataPath, $".health-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"Data folder is not writable: {store.DataPath}", ex, data));
        }

        var space = (disk ?? DiskSpace)(store.DataPath);
        if (space is { } s)
        {
            data["freeBytes"] = s.Free;
            data["totalBytes"] = s.Total;
            if (s.Free < LowSpaceBytes)
                return Task.FromResult(HealthCheckResult.Degraded($"Low disk space: {s.Free / 1024 / 1024} MB free", data: data));
        }
        return Task.FromResult(HealthCheckResult.Healthy("Data folder writable", data));
    }

    public static (long Free, long Total)? DiskSpace(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception) { return null; }
    }
}
