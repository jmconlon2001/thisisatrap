using Microsoft.AspNetCore.StaticFiles;

namespace ThisIsATrap.Endpoints;

/// <summary>Listing, reading and deleting trapped messages.</summary>
public static class MessageEndpoints
{
    public static void MapMessages(this RouteGroupBuilder api)
    {
        // ?inbox=<slug> scopes the list and its stats to one inbox; omitted = all inboxes.
        api.MapGet("/messages", (MailStore store, string? q, bool? unread, bool? attachments, int? skip, int? take, string? inbox) =>
        {
            if (!TryScope(store, inbox, out var inboxId)) return Results.NotFound(new { error = $"No inbox '{inbox}'." });
            var (items, total) = store.List(q, unread ?? false, attachments ?? false, skip ?? 0, take ?? 100, inboxId);
            return Results.Ok(new { items, total, stats = store.Stats(inboxId) });
        });

        api.MapGet("/messages/{id}", async (string id, MailStore store, CancellationToken ct) =>
        {
            var record = store.Get(id);
            if (record is null) return Results.NotFound();
            var message = await MessageParser.LoadAsync(store.RawPath(id), ct);
            var inbox = store.GetInbox(record.InboxId);
            return Results.Ok(MessageParser.ToDetail(record, message) with
            {
                Inbox = inbox?.Slug ?? Inbox.DefaultSlug,
                InboxName = inbox?.Name ?? Inbox.DefaultName,
            });
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

        api.MapGet("/messages/{id}/raw", (string id, bool? download, MailStore store) =>
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

        api.MapPost("/messages/read-all", (MailStore store, EventHub events, string? inbox) =>
        {
            if (!TryScope(store, inbox, out var inboxId)) return Results.NotFound();
            store.MarkAllRead(inboxId);
            events.Publish("changed");
            return Results.NoContent();
        });

        api.MapDelete("/messages/{id}", (string id, MailStore store, EventHub events) =>
        {
            if (!store.Delete(id)) return Results.NotFound();
            events.Publish("deleted", new { id });
            return Results.NoContent();
        });

        api.MapDelete("/messages", (MailStore store, EventHub events, string? inbox) =>
        {
            if (!TryScope(store, inbox, out var inboxId)) return Results.NotFound();
            var removed = store.DeleteAll(inboxId);
            events.Publish("purged", new { removed });
            return Results.Ok(new { removed });
        });
    }

    /// <summary>Resolves an optional ?inbox= slug. Empty means "all inboxes"; an unknown slug fails.</summary>
    internal static bool TryScope(MailStore store, string? slug, out long? inboxId)
    {
        inboxId = null;
        if (string.IsNullOrEmpty(slug)) return true;
        var inbox = store.GetInbox(slug);
        inboxId = inbox?.Id;
        return inbox is not null;
    }

    private static async Task<IResult> SendPartAsync(HttpContext http, MimeKit.MimeEntity part, string fileName, bool download, CancellationToken ct)
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

    private static string FileNameFor(string subject, string ext)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string((string.IsNullOrWhiteSpace(subject) ? "message" : subject).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return (name.Length > 80 ? name[..80] : name) + ext;
    }
}
