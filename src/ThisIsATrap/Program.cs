using ThisIsATrap;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

// Bind the legacy section first so the new one wins wherever both set a value.
builder.Services.Configure<TrapOptions>(builder.Configuration.GetSection(TrapOptions.LegacySection));
builder.Services.Configure<TrapOptions>(builder.Configuration.GetSection(TrapOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MailStore>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<PurgeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PurgeService>());
builder.Services.AddHostedService<SmtpListenerService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/messages", (MailStore store, string? q, bool? unread, bool? attachments, int? skip, int? take) =>
{
    var (items, total) = store.List(q, unread ?? false, attachments ?? false, skip ?? 0, take ?? 100);
    return Results.Ok(new { items, total, stats = store.Stats() });
});

api.MapGet("/messages/{id}", async (string id, MailStore store, CancellationToken ct) =>
{
    var record = store.Get(id);
    if (record is null) return Results.NotFound();
    var message = await MessageParser.LoadAsync(store.RawPath(id), ct);
    return Results.Ok(MessageParser.ToDetail(record, message));
});

// HTML body for the reading pane. Served to a sandboxed iframe with scripts blocked by CSP.
api.MapGet("/messages/{id}/html", async (string id, MailStore store, HttpContext http, CancellationToken ct) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    var message = await MessageParser.LoadAsync(store.RawPath(id), ct);
    http.Response.Headers.ContentSecurityPolicy =
        "default-src 'none'; img-src * data: blob:; style-src * 'unsafe-inline'; font-src * data:; media-src * data:; sandbox allow-popups allow-popups-to-escape-sandbox";
    return Results.Content(MessageParser.RenderHtml(id, message), "text/html; charset=utf-8");
});

api.MapGet("/messages/{id}/raw", async (string id, bool? download, MailStore store) =>
{
    var record = store.Get(id);
    if (record is null) return Results.NotFound();
    var stream = File.OpenRead(store.RawPath(id));
    return download == true
        ? Results.File(stream, "message/rfc822", FileNameFor(record.Subject, ".eml"))
        : Results.File(stream, "text/plain; charset=utf-8");
});

api.MapGet("/messages/{id}/attachments/{index:int}", async (string id, int index, bool? download, MailStore store, HttpContext http, CancellationToken ct) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    var message = await MessageParser.LoadAsync(store.RawPath(id), ct);
    var leaves = MessageParser.Leaves(message);
    if (index < 0 || index >= leaves.Count) return Results.NotFound();
    var part = leaves[index];
    return await SendPartAsync(http, part, MessageParser.FileNameOf(part, index), download == true, ct);
});

api.MapGet("/messages/{id}/cid/{cid}", async (string id, string cid, MailStore store, HttpContext http, CancellationToken ct) =>
{
    if (store.Get(id) is null) return Results.NotFound();
    var message = await MessageParser.LoadAsync(store.RawPath(id), ct);
    var part = MessageParser.FindByContentId(message, Uri.UnescapeDataString(cid));
    return part is null ? Results.NotFound() : await SendPartAsync(http, part, MessageParser.FileNameOf(part, 0), false, ct);
});

api.MapPost("/messages/{id}/read", (string id, bool? read, MailStore store) =>
{
    store.SetRead(id, read ?? true);
    return Results.NoContent();
});

api.MapPost("/messages/read-all", (MailStore store, EventHub events) =>
{
    store.MarkAllRead();
    events.Publish("changed");
    return Results.NoContent();
});

api.MapDelete("/messages/{id}", (string id, MailStore store, EventHub events) =>
{
    if (!store.Delete(id)) return Results.NotFound();
    events.Publish("deleted", new { id });
    return Results.NoContent();
});

api.MapDelete("/messages", (MailStore store, EventHub events) =>
{
    var removed = store.DeleteAll();
    events.Publish("purged", new { removed });
    return Results.Ok(new { removed });
});

api.MapGet("/settings", (MailStore store, Microsoft.Extensions.Options.IOptions<TrapOptions> options, EventHub events) => Results.Ok(new
{
    purge = store.GetPurgeSettings(),
    lastPurgeUtc = PurgeService.LastRunUtc,
    lastPurgeRemoved = PurgeService.LastRemoved,
    smtp = new { port = options.Value.PublicSmtpPort ?? options.Value.SmtpPort, listenPort = options.Value.SmtpPort, host = options.Value.ServerName, status = SmtpListenerService.Status, maxMessageSizeMb = options.Value.MaxMessageSizeMb },
    dataPath = store.DataPath,
    stats = store.Stats(),
    connectedClients = events.ConnectedClients,
}));

api.MapPut("/settings/purge", (PurgeSettings s, MailStore store, PurgeService purge) =>
{
    var clean = new PurgeSettings
    {
        Enabled = s.Enabled,
        MaxAgeHours = Math.Clamp(s.MaxAgeHours, 0, 24 * 365),
        MaxMessages = Math.Clamp(s.MaxMessages, 0, 1_000_000),
        IntervalMinutes = Math.Clamp(s.IntervalMinutes, 1, 24 * 60),
    };
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

app.Run();

static async Task<IResult> SendPartAsync(HttpContext http, MimeKit.MimeEntity part, string fileName, bool download, CancellationToken ct)
{
    var contentType = part.ContentType.MimeType;
    if (contentType == "application/octet-stream" && new FileExtensionContentTypeProvider().TryGetContentType(fileName, out var guessed))
        contentType = guessed;

    // Attached .eml files can't be displayed by browsers; show their source instead.
    if (!download && contentType == "message/rfc822") contentType = "text/plain; charset=utf-8";

    // Attachments come from arbitrary senders: never let one run script on this origin.
    http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; media-src 'self'; object-src 'self'";
    http.Response.Headers.XContentTypeOptions = "nosniff";

    var buffer = new MemoryStream();
    await MessageParser.WriteContentAsync(part, buffer, ct);
    buffer.Position = 0;
    return download
        ? Results.File(buffer, contentType, fileName)
        : Results.Stream(buffer, contentType, enableRangeProcessing: true, fileDownloadName: null);
}

static string FileNameFor(string subject, string ext)
{
    var invalid = Path.GetInvalidFileNameChars();
    var name = new string((string.IsNullOrWhiteSpace(subject) ? "message" : subject).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    return (name.Length > 80 ? name[..80] : name) + ext;
}

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
