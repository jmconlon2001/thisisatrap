using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MailKit.Net.Smtp;
using MimeKit;
using ThisIsATrap.Tests;

namespace ThisIsATrap.IntegrationTests;

/// <summary>Own instance: these tests change the SMTP allowlist and the environment label.</summary>
public sealed class AccessApp : TrapApp
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["ThisIsATrap:EnvironmentLabel"] = "QA",
        ["Logging:LogLevel:Default"] = "Information",   // the log-viewer test needs Information lines
    };
}

public sealed class AccessLogEnvironmentApiTests(AccessApp app) : IClassFixture<AccessApp>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Allowlist_blocks_unlisted_clients_with_554_and_allows_listed_ones()
    {
        using var http = app.CreateClient();
        try
        {
            // The test client connects from 127.0.0.1: exclude loopback and list some other network.
            var put = await http.PutAsJsonAsync("/api/settings/smtp-access", new { enabled = true, allow = new[] { "10.99.0.0/16" }, alwaysAllowLoopback = false }, Ct);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            using (var client = await app.ConnectSmtpAsync())
            {
                var ex = await Assert.ThrowsAsync<SmtpCommandException>(() =>
                    client.SendAsync(FormatOptions.Default, new SendRequest(TestMail.Build(subject: "blocked")), Ct));
                Assert.Equal(554, (int)ex.StatusCode);
                Assert.Contains("5.7.1", ex.Message);
            }

            await http.PutAsJsonAsync("/api/settings/smtp-access", new { enabled = true, allow = new[] { "127.0.0.1" }, alwaysAllowLoopback = false }, Ct);
            await app.SendAsync(TestMail.Build(subject: "allowed-by-list"));
            await app.FindAsync("allowed-by-list");

            var logs = await app.GetJsonAsync("/api/logs?level=Warning&q=allowlist");
            Assert.Contains(logs.GetProperty("entries").EnumerateArray(), e => e.GetProperty("message").GetString()!.Contains("not on the allowlist"));
        }
        finally
        {
            await http.PutAsJsonAsync("/api/settings/smtp-access", new { enabled = false, allow = Array.Empty<string>(), alwaysAllowLoopback = true }, Ct);
        }
    }

    [Fact]
    public async Task Allowlist_api_validates_and_reports_the_callers_address()
    {
        using var http = app.CreateClient();
        var bad = await http.PutAsJsonAsync("/api/settings/smtp-access", new { enabled = true, allow = new[] { "not-an-ip" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not-an-ip", await bad.Content.ReadAsStringAsync(Ct));

        var current = await app.GetJsonAsync("/api/settings/smtp-access");
        Assert.True(current.TryGetProperty("yourAddress", out _));
        Assert.False(current.GetProperty("settings").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Logs_show_smtp_activity_and_can_be_tailed_downloaded_and_cleared()
    {
        using var http = app.CreateClient();
        var before = (await app.GetJsonAsync("/api/logs?level=Debug")).GetProperty("lastSeq").GetInt64();

        await app.SendAsync(TestMail.Build(subject: "logged-message"));
        await app.FindAsync("logged-message");

        var tail = await app.GetJsonAsync($"/api/logs?after={before}&level=Information");
        var messages = tail.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("message").GetString()!).ToList();
        Assert.Contains(messages, m => m.StartsWith("SMTP connection from 127.0.0.1"));
        Assert.Contains(messages, m => m.Contains("logged-message"));
        Assert.True(tail.GetProperty("capacity").GetInt32() >= 100);

        var file = await http.GetAsync("/api/logs/download?q=logged-message", Ct);
        Assert.Equal("text/plain", file.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".log", file.Content.Headers.ContentDisposition!.FileNameStar ?? file.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Contains("logged-message", await file.Content.ReadAsStringAsync(Ct));

        var cleared = await (await http.DeleteAsync("/api/logs", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(cleared.GetProperty("removed").GetInt32() > 0);
    }

    [Fact]
    public async Task Environment_label_comes_from_config_and_can_be_overridden()
    {
        using var http = app.CreateClient();
        var env = (await app.GetJsonAsync("/api/settings")).GetProperty("environment");
        Assert.Equal("QA", env.GetProperty("label").GetString());
        Assert.Equal("amber", env.GetProperty("color").GetString());
        Assert.Equal("config", env.GetProperty("source").GetString());
        Assert.Equal("QA", (await app.GetJsonAsync("/api/status")).GetProperty("app").GetProperty("label").GetString());

        try
        {
            var saved = await (await http.PutAsJsonAsync("/api/settings/environment", new { label = "Stage", color = "blue" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("Stage", saved.GetProperty("label").GetString());
            Assert.Equal("settings", saved.GetProperty("source").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/settings/environment", new { label = "x", color = "red;background:url(x)" }, Ct)).StatusCode);
        }
        finally
        {
            var reset = await (await http.PutAsJsonAsync("/api/settings/environment", new { label = (string?)null }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
            Assert.Equal("config", reset.GetProperty("source").GetString());
        }
    }
}
