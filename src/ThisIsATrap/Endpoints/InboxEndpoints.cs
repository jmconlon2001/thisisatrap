namespace ThisIsATrap.Endpoints;

public sealed record InboxCreate(string? Name);
public sealed record InboxUpdate(string? Name, PurgeSettings? Purge);

/// <summary>Creating, renaming, configuring and deleting inboxes.</summary>
public static class InboxEndpoints
{
    public static void MapInboxes(this RouteGroupBuilder api)
    {
        api.MapGet("/inboxes", (MailStore store) => Results.Ok(store.ListInboxes()));

        api.MapGet("/inboxes/{slug}", (string slug, MailStore store) =>
            store.ListInboxes().FirstOrDefault(i => i.Slug == slug) is { } inbox ? Results.Ok(inbox) : Results.NotFound());

        api.MapPost("/inboxes", (InboxCreate body, MailStore store, EventHub events) =>
        {
            if (InboxSlug.Normalize(body.Name) is null)
                return Results.BadRequest(new { error = "An inbox name needs at least one letter or digit." });
            var (inbox, created) = store.CreateInbox(body.Name!);
            if (!created) return Results.Conflict(new { error = $"An inbox with the key '{inbox.Slug}' already exists.", inbox });
            events.Publish("inboxes");
            return Results.Created($"/api/inboxes/{inbox.Slug}", inbox);
        });

        // Rename, and set custom purge rules (purge: null = use the global rules).
        api.MapPut("/inboxes/{slug}", (string slug, InboxUpdate body, MailStore store, EventHub events, PurgeService purge) =>
        {
            var inbox = store.GetInbox(slug);
            if (inbox is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "Name is required." });
            var updated = store.UpdateInbox(inbox.Id, body.Name, body.Purge is null ? null : SettingsEndpoints.Clamp(body.Purge));
            events.Publish("inboxes");
            purge.Wake();
            return Results.Ok(updated);
        });

        api.MapDelete("/inboxes/{slug}", (string slug, MailStore store, EventHub events) =>
        {
            var inbox = store.GetInbox(slug);
            if (inbox is null) return Results.NotFound();
            if (inbox.IsDefault) return Results.BadRequest(new { error = "The Default inbox can't be deleted." });
            var removed = store.DeleteInbox(inbox.Id) ?? 0;
            events.Publish("inboxes");
            events.Publish("purged", new { removed });
            return Results.Ok(new { removed });
        });
    }
}
