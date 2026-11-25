using System.Net;
using ThisIsATrap.Tests;

namespace ThisIsATrap.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class AnalysisApiTests(TrapApp app)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> Send(string? html, string? text = "Plain body")
    {
        var subject = "analysis-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject, html: html, text: text));
        return (await app.FindAsync(subject)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Html_check_reports_features_clients_and_score()
    {
        var id = await Send("<html><head><style>@media (max-width:600px){.a{display:block}}</style></head><body><div style=\"display:flex;box-shadow:0 0 2px #000\">x</div></body></html>");

        var r = await app.GetJsonAsync($"/api/messages/{id}/html-check");

        Assert.True(r.GetProperty("hasHtml").GetBoolean());
        var slugs = r.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("slug").GetString()).ToList();
        Assert.Contains("css-display-flex", slugs);
        Assert.Contains("css-box-shadow", slugs);
        Assert.Contains("css-at-media", slugs);
        Assert.InRange(r.GetProperty("score").GetDouble(), 1, 99.9);
        Assert.Equal("apple-mail", r.GetProperty("clients")[0].GetProperty("family").GetString());
        Assert.StartsWith("https://www.caniemail.com/", r.GetProperty("features")[0].GetProperty("url").GetString());

        // Second call is served from the cache and is identical.
        var again = await app.GetJsonAsync($"/api/messages/{id}/html-check");
        Assert.Equal(r.GetRawText(), again.GetRawText());
    }

    [Fact]
    public async Task Html_check_of_text_only_mail_is_empty()
    {
        var id = await Send(html: null);
        var r = await app.GetJsonAsync($"/api/messages/{id}/html-check");
        Assert.False(r.GetProperty("hasHtml").GetBoolean());
        Assert.Equal(0, r.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task Spam_report_uses_builtin_rules_when_spamassassin_is_not_configured()
    {
        var subject = "SPAM SPAM SPAM SPAM!!! " + Guid.NewGuid().ToString("N").ToUpperInvariant();
        await app.SendAsync(TestMail.Build(subject: subject, text: null,
            html: "<p>Congratulations winner, act now! <a href=\"https://bit.ly/x\">claim</a> <a href=\"http://localhost:5000/reset\">reset</a></p>",
            attachments: [("setup.exe", "application/octet-stream", [1])]));
        var id = (await app.FindAsync(subject)).GetProperty("id").GetString();

        var r = await app.GetJsonAsync($"/api/messages/{id}/spam");

        Assert.Equal("builtin", r.GetProperty("source").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, r.GetProperty("spamAssassin").ValueKind);
        Assert.Equal("Likely spam", r.GetProperty("verdict").GetString());
        Assert.True(r.GetProperty("score").GetDouble() >= 5);
        var ids = r.GetProperty("rules").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
        Assert.Contains("RISKY_ATTACHMENT", ids);
        Assert.Contains("URL_SHORTENER", ids);
        Assert.Contains("LOCAL_URL", ids);
        Assert.Contains("SUBJ_ALL_CAPS", ids);
    }

    [Fact]
    public async Task Clean_mail_scores_zero()
    {
        var id = await Send("<p>Your report for this week is ready and everything looks fine. Nothing else to do here.</p>",
            "Your report for this week is ready and everything looks fine. Nothing else to do here.");
        var r = await app.GetJsonAsync($"/api/messages/{id}/spam");
        Assert.Equal(0, r.GetProperty("score").GetDouble());
        Assert.Equal("Looks clean", r.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Html_check_for_unknown_message_is_404()
    {
        using var http = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/messages/{Guid.NewGuid():N}/html-check", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/messages/{Guid.NewGuid():N}/spam", Ct)).StatusCode);
    }
}

/// <summary>An instance pointed at a fake spamd that always answers with a fixed report.</summary>
public sealed class SpamAssassinApp : TrapApp
{
    private readonly System.Net.Sockets.TcpListener _spamd = new(System.Net.IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public SpamAssassinApp()
    {
        _spamd.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var client = await _spamd.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    await reader.ReadToEndAsync(_stop.Token);
                    await stream.WriteAsync("SPAMD/1.1 0 EX_OK\r\nSpam: False ; 2.5 / 5.0\r\n\r\n 2.5 TEST_RULE    A test rule\r\n"u8.ToArray(), _stop.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
            }
        });
    }

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["ThisIsATrap:SpamAssassin:Host"] = "127.0.0.1",
        ["ThisIsATrap:SpamAssassin:Port"] = ((System.Net.IPEndPoint)_spamd.LocalEndpoint).Port.ToString(),
    };

    public override async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _spamd.Stop();
        _spamd.Dispose();
        await base.DisposeAsync();
    }
}

public sealed class SpamAssassinApiTests(SpamAssassinApp app) : IClassFixture<SpamAssassinApp>
{
    [Fact]
    public async Task Spamassassin_score_is_used_when_configured()
    {
        await app.SendAsync(TestMail.Build(subject: "sa-check"));
        var id = (await app.FindAsync("sa-check")).GetProperty("id").GetString();

        var r = await app.GetJsonAsync($"/api/messages/{id}/spam");

        Assert.Equal("spamassassin", r.GetProperty("source").GetString());
        Assert.Equal(2.5, r.GetProperty("score").GetDouble());
        Assert.Equal("Some issues", r.GetProperty("verdict").GetString());
        var sa = r.GetProperty("spamAssassin");
        Assert.True(sa.GetProperty("available").GetBoolean());
        Assert.Equal("TEST_RULE", sa.GetProperty("rules")[0].GetProperty("id").GetString());
    }
}
