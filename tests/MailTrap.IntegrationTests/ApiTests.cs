using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailTrap.Tests;

namespace MailTrap.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class ApiTests(MailTrapApp app)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> SendRichAsync(string subject)
    {
        await app.SendAsync(TestMail.Build(
            subject: subject,
            html: "<html><head></head><body><img src=\"cid:logo\"><p>Hi</p><script>alert(1)</script></body></html>",
            attachments: [TestMail.Csv()],
            inlineImages: [("logo", TestMail.OnePixelPng)]));
        return (await app.FindAsync(subject)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Root_serves_the_web_client()
    {
        using var http = app.CreateClient();
        var html = await http.GetStringAsync("/", Ct);
        Assert.Contains("<title>", html);
        Assert.Contains("js/app.js", html);
    }

    [Fact]
    public async Task Detail_lists_attachments_headers_and_bodies()
    {
        var id = await SendRichAsync("detail-" + Guid.NewGuid());
        var d = await app.GetJsonAsync($"/api/messages/{id}");

        Assert.True(d.GetProperty("hasHtml").GetBoolean());
        Assert.Equal("Plain body", d.GetProperty("text").GetString());
        var names = d.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("fileName").GetString()).ToList();
        Assert.Contains("report.csv", names);
        Assert.Contains("logo.png", names);
        Assert.Contains(d.GetProperty("headers").EnumerateArray(), h => h.GetProperty("name").GetString() == "Subject");
    }

    [Fact]
    public async Task Html_is_served_sandboxed_with_cid_rewritten()
    {
        var id = await SendRichAsync("html-" + Guid.NewGuid());
        using var http = app.CreateClient();

        var response = await http.GetAsync($"/api/messages/{id}/html", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var csp = string.Join(";", response.Headers.GetValues("Content-Security-Policy"));

        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("sandbox", csp);
        Assert.DoesNotContain("script-src", csp); // scripts fall back to default-src 'none'
        Assert.Contains("src=\"cid/logo\"", body);
        Assert.Contains("<base target=\"_blank\">", body);
    }

    [Fact]
    public async Task Cid_endpoint_returns_the_inline_image()
    {
        var id = await SendRichAsync("cid-" + Guid.NewGuid());
        using var http = app.CreateClient();

        var response = await http.GetAsync($"/api/messages/{id}/cid/logo", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(TestMail.OnePixelPng, await response.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/messages/{id}/cid/nope", Ct)).StatusCode);
    }

    [Fact]
    public async Task Attachment_is_served_inline_or_as_download_and_never_as_active_content()
    {
        var id = await SendRichAsync("att-" + Guid.NewGuid());
        var d = await app.GetJsonAsync($"/api/messages/{id}");
        var index = d.GetProperty("attachments").EnumerateArray().First(a => a.GetProperty("fileName").GetString() == "report.csv").GetProperty("index").GetInt32();
        using var http = app.CreateClient();

        var inline = await http.GetAsync($"/api/messages/{id}/attachments/{index}", Ct);
        var download = await http.GetAsync($"/api/messages/{id}/attachments/{index}?download=true", Ct);

        Assert.Equal("id,total\r\n1,10\r\n", await inline.Content.ReadAsStringAsync(Ct));
        Assert.Equal("nosniff", inline.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'none'", string.Join(";", inline.Headers.GetValues("Content-Security-Policy")));
        Assert.Null(inline.Content.Headers.ContentDisposition);
        Assert.Equal("report.csv", download.Content.Headers.ContentDisposition!.FileNameStar ?? download.Content.Headers.ContentDisposition.FileName?.Trim('"'));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/messages/{id}/attachments/99", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/messages/{id}/attachments/-1", Ct)).StatusCode);
    }

    [Fact]
    public async Task Raw_source_and_eml_download()
    {
        var subject = "raw-" + Guid.NewGuid();
        var id = await SendRichAsync(subject);
        using var http = app.CreateClient();

        var raw = await http.GetAsync($"/api/messages/{id}/raw", Ct);
        var eml = await http.GetAsync($"/api/messages/{id}/raw?download=true", Ct);

        Assert.Equal("text/plain", raw.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"Subject: {subject}", await raw.Content.ReadAsStringAsync(Ct));
        Assert.Equal("message/rfc822", eml.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".eml", eml.Content.Headers.ContentDisposition!.FileNameStar ?? eml.Content.Headers.ContentDisposition.FileName!.Trim('"'));
    }

    [Theory]
    [InlineData("/api/messages/{0}")]
    [InlineData("/api/messages/{0}/html")]
    [InlineData("/api/messages/{0}/raw")]
    [InlineData("/api/messages/{0}/attachments/0")]
    [InlineData("/api/messages/{0}/cid/x")]
    public async Task Unknown_or_malicious_ids_are_404(string template)
    {
        using var http = app.CreateClient();
        foreach (var id in new[] { Guid.NewGuid().ToString("N"), "..%2F..%2Fappsettings", "not-an-id" })
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(string.Format(template, id), Ct)).StatusCode);
    }

    [Fact]
    public async Task Read_state_can_be_toggled_and_filtered()
    {
        var subject = "read-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject));
        var id = (await app.FindAsync(subject)).GetProperty("id").GetString();
        using var http = app.CreateClient();

        (await http.PostAsync($"/api/messages/{id}/read", null, Ct)).EnsureSuccessStatusCode();
        Assert.True((await app.FindAsync(subject)).GetProperty("isRead").GetBoolean());

        (await http.PostAsync($"/api/messages/{id}/read?read=false", null, Ct)).EnsureSuccessStatusCode();
        var unread = await app.GetJsonAsync("/api/messages?unread=true&q=" + subject);
        Assert.Equal(1, unread.GetProperty("total").GetInt32());

        (await http.PostAsync("/api/messages/read-all", null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await app.GetJsonAsync("/api/messages")).GetProperty("stats").GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task Attachment_filter_and_paging()
    {
        var tag = Guid.NewGuid().ToString("N");
        await app.SendAsync(TestMail.Build(subject: tag + "-plain"));
        await app.SendAsync(TestMail.Build(subject: tag + "-att", attachments: [TestMail.Pdf()]));
        await app.FindAsync(tag + "-plain");
        await app.FindAsync(tag + "-att");

        var withAtt = await app.GetJsonAsync($"/api/messages?attachments=true&q={tag}");
        var page = await app.GetJsonAsync($"/api/messages?q={tag}&take=1&skip=1");

        Assert.Equal(1, withAtt.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Single(page.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Delete_one_then_all()
    {
        var subject = "del-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject));
        var id = (await app.FindAsync(subject)).GetProperty("id").GetString();
        using var http = app.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"/api/messages/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/api/messages/{id}", Ct)).StatusCode);
        Assert.False(File.Exists(Path.Combine(app.DataPath, "messages", id + ".eml")));

        await app.SendAsync(TestMail.Build(subject: subject + "-2"));
        await app.FindAsync(subject + "-2");
        var all = await (await http.DeleteAsync("/api/messages", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(all.GetProperty("removed").GetInt32() >= 1);
        Assert.Equal(0, (await app.GetJsonAsync("/api/messages")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Settings_report_smtp_status_and_storage()
    {
        var s = await app.GetJsonAsync("/api/settings");

        Assert.StartsWith("listening", s.GetProperty("smtp").GetProperty("status").GetString());
        Assert.Equal(app.SmtpPort, s.GetProperty("smtp").GetProperty("port").GetInt32());
        Assert.Equal(app.SmtpPort, s.GetProperty("smtp").GetProperty("listenPort").GetInt32());
        Assert.Equal(1, s.GetProperty("smtp").GetProperty("maxMessageSizeMb").GetInt32());
        Assert.Equal(app.DataPath, s.GetProperty("dataPath").GetString());
    }

    [Fact]
    public async Task Purge_settings_are_clamped_and_persisted()
    {
        using var http = app.CreateClient();
        var response = await http.PutAsJsonAsync("/api/settings/purge",
            new { enabled = false, maxAgeHours = -5, maxMessages = 99_999_999, intervalMinutes = 0 }, Ct);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(0, saved.GetProperty("maxAgeHours").GetInt32());
        Assert.Equal(1_000_000, saved.GetProperty("maxMessages").GetInt32());
        Assert.Equal(1, saved.GetProperty("intervalMinutes").GetInt32());
        var purge = (await app.GetJsonAsync("/api/settings")).GetProperty("purge");
        Assert.False(purge.GetProperty("enabled").GetBoolean());

        // Restore defaults for other tests.
        await http.PutAsJsonAsync("/api/settings/purge", new { enabled = true, maxAgeHours = 72, maxMessages = 1000, intervalMinutes = 15 }, Ct);
    }

    [Fact]
    public async Task Purge_now_applies_rules()
    {
        using var http = app.CreateClient();
        await app.ClearAsync();
        var tag = Guid.NewGuid().ToString("N");
        for (var i = 0; i < 3; i++) { await app.SendAsync(TestMail.Build(subject: $"{tag}-{i}")); await app.FindAsync($"{tag}-{i}"); }
        await http.PutAsJsonAsync("/api/settings/purge", new { enabled = false, maxAgeHours = 0, maxMessages = 1, intervalMinutes = 15 }, Ct);

        var result = await (await http.PostAsync("/api/purge", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(2, result.GetProperty("removed").GetInt32());
        Assert.Equal(1, (await app.GetJsonAsync("/api/messages")).GetProperty("total").GetInt32());
        await http.PutAsJsonAsync("/api/settings/purge", new { enabled = true, maxAgeHours = 72, maxMessages = 1000, intervalMinutes = 15 }, Ct);
    }

    [Fact]
    public async Task Events_stream_pushes_new_mail()
    {
        using var http = app.CreateClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        Assert.Equal("retry: 3000", await reader.ReadLineAsync(cts.Token));

        var subject = "sse-" + Guid.NewGuid();
        _ = Task.Run(() => app.SendAsync(TestMail.Build(subject: subject)), cts.Token);

        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null || !line.StartsWith("data: ")) continue;
            using var evt = JsonDocument.Parse(line[6..]);
            if (evt.RootElement.GetProperty("type").GetString() != "message") continue;
            if (evt.RootElement.GetProperty("payload").GetProperty("subject").GetString() == subject) break;
        }
    }
}
