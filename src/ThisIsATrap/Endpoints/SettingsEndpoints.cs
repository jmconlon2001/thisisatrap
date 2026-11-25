using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using ThisIsATrap.Health;

namespace ThisIsATrap.Endpoints;

/// <summary>Global settings, purge, and the live event stream.</summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this RouteGroupBuilder api)
    {
        api.MapGet("/settings", (MailStore store, IOptions<TrapOptions> options, EventHub events, RuntimeStats stats) => Results.Ok(new
        {
            purge = store.GetPurgeSettings(),
            lastPurgeUtc = PurgeService.LastRunUtc,
            lastPurgeRemoved = PurgeService.LastRemoved,
            smtp = new
            {
                port = options.Value.PublicSmtpPort ?? options.Value.SmtpPort,
                listenPort = options.Value.SmtpPort,
                host = options.Value.ServerName,
                status = stats.SmtpStatus,
                maxMessageSizeMb = options.Value.MaxMessageSizeMb,
                autoCreateInboxes = options.Value.AutoCreateInboxes,
                routingHeader = Inbox.RoutingHeader,
            },
            dataPath = store.DataPath,
            stats = store.Stats(),
            connectedClients = events.ConnectedClients,
        }));

        api.MapPut("/settings/purge", (PurgeSettings s, MailStore store, PurgeService purge) =>
        {
            var clean = Clamp(s);
            store.SavePurgeSettings(clean);
            purge.Wake();
            return Results.Ok(clean);
        });

        api.MapPost("/purge", (PurgeService purge) => Results.Ok(new { removed = purge.RunOnce() }));

        // Live updates (new mail toasts) via Server-Sent Events.
        api.MapGet("/events", async (HttpContext http, EventHub hub, IHostApplicationLifetime lifetime) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
            var (id, reader) = hub.Subscribe();
            try
            {
                await http.Response.WriteAsync("retry: 3000\n\n", stop.Token);
                await http.Response.Body.FlushAsync(stop.Token);
                while (!stop.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    wait.CancelAfter(TimeSpan.FromSeconds(20));
                    string line;
                    try { line = $"data: {await reader.ReadAsync(wait.Token)}\n\n"; }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested) { line = ": ping\n\n"; }
                    await http.Response.WriteAsync(line, stop.Token);
                    await http.Response.Body.FlushAsync(stop.Token);
                }
            }
            catch (OperationCanceledException) { }
            finally { hub.Unsubscribe(id); }
        });
    }

    /// <summary>Keeps user-entered purge rules within sane bounds.</summary>
    public static PurgeSettings Clamp(PurgeSettings s) => new()
    {
        Enabled = s.Enabled,
        MaxAgeHours = Math.Clamp(s.MaxAgeHours, 0, 24 * 365),
        MaxMessages = Math.Clamp(s.MaxMessages, 0, 1_000_000),
        IntervalMinutes = Math.Clamp(s.IntervalMinutes, 1, 24 * 60),
    };
}
