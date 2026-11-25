using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ThisIsATrap.Tests;

namespace ThisIsATrap.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class InboxApiTests(TrapApp app)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    private async Task<JsonElement?> FindInbox(string slug)
    {
        var all = await app.GetJsonAsync("/api/inboxes");
        foreach (var i in all.EnumerateArray()) if (i.GetProperty("slug").GetString() == slug) return i;
        return null;
    }

    [Fact]
    public async Task Smtp_username_creates_and_routes_to_an_inbox()
    {
        var user = Unique("billing");
        var subject = "routed-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject), user: user);

        var item = await app.FindAsync(subject);
        Assert.Equal(user, item.GetProperty("inbox").GetString());

        var scoped = await app.GetJsonAsync($"/api/messages?inbox={user}");
        Assert.Equal(1, scoped.GetProperty("total").GetInt32());
        Assert.Equal(1, scoped.GetProperty("stats").GetProperty("total").GetInt32());

        var inbox = await FindInbox(user);
        Assert.NotNull(inbox);
        Assert.Equal(1, inbox!.Value.GetProperty("unread").GetInt32());

        var detail = await app.GetJsonAsync($"/api/messages/{item.GetProperty("id").GetString()}");
        Assert.Equal(user, detail.GetProperty("inbox").GetString());
        Assert.Equal(user, detail.GetProperty("inboxName").GetString());
    }

    [Fact]
    public async Task Routing_header_is_used_without_authentication()
    {
        var key = Unique("hdr");
        var subject = "header-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject, headers: new Dictionary<string, string> { [Inbox.RoutingHeader] = key }));

        Assert.Equal(key, (await app.FindAsync(subject)).GetProperty("inbox").GetString());
    }

    [Fact]
    public async Task Unrouted_mail_lands_in_default()
    {
        var subject = "plain-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject));
        Assert.Equal(Inbox.DefaultSlug, (await app.FindAsync(subject)).GetProperty("inbox").GetString());
    }

    [Fact]
    public async Task Create_conflict_and_validation()
    {
        using var http = app.CreateClient();
        var name = "Created " + Guid.NewGuid().ToString("N")[..6];

        var created = await http.PostAsJsonAsync("/api/inboxes", new { name }, Ct);
        var dup = await http.PostAsJsonAsync("/api/inboxes", new { name = name.ToUpperInvariant() }, Ct);
        var bad = await http.PostAsJsonAsync("/api/inboxes", new { name = "!!!" }, Ct);
        var missing = await http.PostAsJsonAsync("/api/inboxes", new { }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var inbox = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(name, inbox.GetProperty("name").GetString());
        Assert.Equal($"/api/inboxes/{inbox.GetProperty("slug").GetString()}", created.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Update_renames_and_sets_clamped_purge_rules()
    {
        using var http = app.CreateClient();
        var slug = Unique("upd");
        await http.PostAsJsonAsync("/api/inboxes", new { name = slug }, Ct);

        var response = await http.PutAsJsonAsync($"/api/inboxes/{slug}",
            new { name = "Renamed", purge = new { enabled = true, maxAgeHours = -1, maxMessages = 5, intervalMinutes = 0 } }, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Renamed", body.GetProperty("name").GetString());
        Assert.Equal(slug, body.GetProperty("slug").GetString());
        Assert.Equal(0, body.GetProperty("purge").GetProperty("maxAgeHours").GetInt32());
        Assert.Equal(5, body.GetProperty("purge").GetProperty("maxMessages").GetInt32());

        var cleared = await (await http.PutAsJsonAsync($"/api/inboxes/{slug}", new { name = "Renamed", purge = (object?)null }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("purge").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await http.PutAsJsonAsync("/api/inboxes/nope-nope", new { name = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync($"/api/inboxes/{slug}", new { name = " " }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/inboxes/{slug}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/inboxes/nope-nope", Ct)).StatusCode);
    }

    [Fact]
    public async Task Delete_inbox_removes_its_mail_and_files()
    {
        using var http = app.CreateClient();
        var user = Unique("gone");
        var subject = "to-delete-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject), user: user);
        var id = (await app.FindAsync(subject)).GetProperty("id").GetString();

        var result = await (await http.DeleteAsync($"/api/inboxes/{user}", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(1, result.GetProperty("removed").GetInt32());
        Assert.Null(await FindInbox(user));
        Assert.False(File.Exists(Path.Combine(app.DataPath, "messages", id + ".eml")));
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/api/inboxes/{user}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.DeleteAsync("/api/inboxes/default", Ct)).StatusCode);
    }

    [Fact]
    public async Task Read_all_and_delete_all_respect_the_inbox_scope()
    {
        using var http = app.CreateClient();
        var a = Unique("sa");
        var b = Unique("sb");
        await app.SendAsync(TestMail.Build(subject: a + "-m"), user: a);
        await app.SendAsync(TestMail.Build(subject: b + "-m"), user: b);
        await app.FindAsync(a + "-m");
        await app.FindAsync(b + "-m");

        (await http.PostAsync($"/api/messages/read-all?inbox={a}", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await FindInbox(a))!.Value.GetProperty("unread").GetInt32());
        Assert.Equal(1, (await FindInbox(b))!.Value.GetProperty("unread").GetInt32());

        (await http.DeleteAsync($"/api/messages?inbox={a}", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await FindInbox(a))!.Value.GetProperty("total").GetInt32());
        Assert.Equal(1, (await FindInbox(b))!.Value.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("GET", "/api/messages?inbox=does-not-exist")]
    [InlineData("POST", "/api/messages/read-all?inbox=does-not-exist")]
    [InlineData("DELETE", "/api/messages?inbox=does-not-exist")]
    public async Task Unknown_inbox_scope_is_404(string method, string url)
    {
        using var http = app.CreateClient();
        var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), url), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Settings_describe_routing()
    {
        var smtp = (await app.GetJsonAsync("/api/settings")).GetProperty("smtp");
        Assert.True(smtp.GetProperty("autoCreateInboxes").GetBoolean());
        Assert.Equal(Inbox.RoutingHeader, smtp.GetProperty("routingHeader").GetString());
    }
}

/// <summary>An install with automatic inbox creation switched off.</summary>
public sealed class NoAutoCreateApp : TrapApp
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["ThisIsATrap:AutoCreateInboxes"] = "false" };
}

public sealed class NoAutoCreateTests(NoAutoCreateApp app) : IClassFixture<NoAutoCreateApp>
{
    [Fact]
    public async Task Unknown_usernames_go_to_default_but_existing_inboxes_still_route()
    {
        using var http = app.CreateClient();
        await http.PostAsJsonAsync("/api/inboxes", new { name = "known" }, TestContext.Current.CancellationToken);

        await app.SendAsync(TestMail.Build(subject: "unknown-user"), user: "stranger");
        await app.SendAsync(TestMail.Build(subject: "known-user"), user: "known");

        Assert.Equal(Inbox.DefaultSlug, (await app.FindAsync("unknown-user")).GetProperty("inbox").GetString());
        Assert.Equal("known", (await app.FindAsync("known-user")).GetProperty("inbox").GetString());
        var inboxes = await app.GetJsonAsync("/api/inboxes");
        Assert.DoesNotContain(inboxes.EnumerateArray(), i => i.GetProperty("slug").GetString() == "stranger");
    }
}
