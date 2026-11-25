using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ThisIsATrap.Health;

namespace ThisIsATrap.Endpoints;

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static IServiceCollection AddTrapHealth(this IServiceCollection services)
    {
        services.AddSingleton<RuntimeStats>();
        services.AddHealthChecks()
            .AddCheck<SmtpHealthCheck>("smtp", tags: [ReadyTag])
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag])
            .AddCheck<StorageHealthCheck>("storage", tags: [ReadyTag]);
        services.AddSingleton<SmtpHealthCheck>();
        services.AddSingleton<DatabaseHealthCheck>();
        services.AddSingleton(sp => new StorageHealthCheck(sp.GetRequiredService<MailStore>()));
        return services;
    }

    public static void MapTrapHealth(this WebApplication app)
    {
        // Liveness: the process is up and serving HTTP. No dependencies are checked.
        app.MapHealthChecks("/healthz", new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = (http, report) => http.Response.WriteAsync(report.Status.ToString()),
        });

        // Readiness: SMTP listening, database reachable, data folder writable. 503 when any is unhealthy.
        app.MapHealthChecks("/readyz", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains(ReadyTag),
            ResponseWriter = (http, report) =>
            {
                http.Response.ContentType = "application/json; charset=utf-8";
                return http.Response.WriteAsync(JsonSerializer.Serialize(ToDto(report), Json));
            },
        });

        app.MapGet("/api/status", async (HealthCheckService health, MailStore store, RuntimeStats stats, EventHub events,
            IOptions<TrapOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
        {
            var report = await health.CheckHealthAsync(c => c.Tags.Contains(ReadyTag), ct);
            var o = options.Value;
            var purge = store.GetPurgeSettings();
            var mail = store.Stats();
            var disk = StorageHealthCheck.DiskSpace(store.DataPath);
            var process = System.Diagnostics.Process.GetCurrentProcess();

            return Results.Ok(new
            {
                health = ToDto(report),
                app = new
                {
                    name = "ThisIsATrap",
                    version = Version,
                    environment = env.EnvironmentName,
                    host = Environment.MachineName,
                    os = RuntimeInformation.OSDescription,
                    runtime = RuntimeInformation.FrameworkDescription,
                    hosting = Hosting,
                    processId = Environment.ProcessId,
                    memoryBytes = process.WorkingSet64,
                    startedUtc = stats.StartedUtc,
                    uptimeSeconds = (long)stats.Uptime.TotalSeconds,
                },
                smtp = new
                {
                    status = stats.SmtpStatus,
                    listening = stats.SmtpListening,
                    serverName = o.ServerName,
                    listenPort = o.SmtpPort,
                    port = o.PublicSmtpPort ?? o.SmtpPort,
                    maxMessageSizeMb = o.MaxMessageSizeMb,
                    messagesReceived = stats.MessagesReceived,
                    messagesRejected = stats.MessagesRejected,
                    lastMessageUtc = stats.LastMessageUtc,
                },
                storage = new
                {
                    dataPath = store.DataPath,
                    messages = mail.Total,
                    unread = mail.Unread,
                    messageBytes = mail.TotalBytes,
                    databaseBytes = store.DatabaseBytes(),
                    diskFreeBytes = disk?.Free,
                    diskTotalBytes = disk?.Total,
                },
                purge = new
                {
                    purge.Enabled,
                    purge.MaxAgeHours,
                    purge.MaxMessages,
                    purge.IntervalMinutes,
                    lastRunUtc = PurgeService.LastRunUtc,
                    lastRemoved = PurgeService.LastRemoved,
                    nextRunUtc = purge.Enabled && PurgeService.LastRunUtc is { } last ? last.AddMinutes(purge.IntervalMinutes) : (DateTimeOffset?)null,
                },
                events = new { connectedClients = events.ConnectedClients },
            });
        });
    }

    public static string Version { get; } =
        typeof(HealthEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private static string Hosting =>
        Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? "Docker"
        : Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH") is not null || Environment.GetEnvironmentVariable("APP_POOL_ID") is not null ? "IIS"
        : "Kestrel";

    private static object ToDto(HealthReport report) => new
    {
        status = report.Status.ToString(),
        totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            description = e.Value.Description,
            durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
            error = e.Value.Exception?.Message,
            data = e.Value.Data.Count == 0 ? null : e.Value.Data,
        }),
    };
}
