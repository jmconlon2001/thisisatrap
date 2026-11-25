using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using ThisIsATrap.Health;

namespace ThisIsATrap.Endpoints;

/// <summary>Global settings, purge, and the live event stream.</summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this RouteGroupBuilder api)
    {
        api.MapGet("/settings", (MailStore store, IOptions<TrapOptions> options, EventHub events, RuntimeStats stats, SmtpTls tls) => Results.Ok(new
        {
            tls = TlsDto(tls),
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

        // ---- webhooks (secrets are write-only: responses only say whether one is set)
        api.MapGet("/settings/webhooks", (WebhookService hooks) => Results.Ok(hooks.Views()));

        api.MapPut("/settings/webhooks", (List<WebhookInput> body, WebhookService hooks) =>
        {
            if (body.Count > WebhookService.MaxWebhooks) return Results.BadRequest(new { error = $"At most {WebhookService.MaxWebhooks} webhooks." });
            try { return Results.Ok(hooks.Save(body)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        api.MapPost("/settings/webhooks/{id}/test", async (string id, WebhookService hooks, CancellationToken ct) =>
            await hooks.TestAsync(id, ct) is { } status ? Results.Ok(status) : Results.NotFound());

        // ---- SMTP error simulation
        api.MapGet("/settings/smtp-rules", (SmtpRules rules) => Results.Ok(rules.Get()));
        api.MapGet("/settings/smtp-rules/defaults", () => Results.Ok(SmtpRules.Defaults));
        api.MapPut("/settings/smtp-rules", (List<SmtpRule> body, SmtpRules rules) =>
            body.Count > SmtpRules.MaxRules
                ? Results.BadRequest(new { error = $"At most {SmtpRules.MaxRules} rules." })
                : Results.Ok(rules.Save(body)));

        // Public part of the SMTP TLS certificate, so clients can trust a self-signed one. Never the private key.
        api.MapGet("/tls/certificate", (SmtpTls tls, string? format) =>
        {
            if (!tls.Enabled || tls.Certificate is not { } cert) return Results.NotFound(new { error = "SMTP TLS is not enabled." });
            return format == "pem"
                ? Results.File(System.Text.Encoding.ASCII.GetBytes(cert.ExportCertificatePem() + "\n"), "application/x-pem-file", "thisisatrap-smtp.pem")
                : Results.File(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert), "application/pkix-cert", "thisisatrap-smtp.cer");
        });

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

    internal static object TlsDto(SmtpTls tls)
    {
        var o = tls.Options;
        CertificateInfo? info = null;
        string? error = null;
        if (tls.Enabled)
        {
            try { info = tls.Info; }
            catch (Exception ex) { error = ex.Message; }
        }
        return new
        {
            enabled = tls.Enabled,
            startTls = o.StartTls,
            smtpsPort = o.PublicSmtpsPort ?? o.SmtpsPort,
            smtpsListenPort = o.SmtpsPort,
            certificate = info,
            error,
        };
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
