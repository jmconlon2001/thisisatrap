using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MailKit;
using MailKit.Net.Smtp;
using MimeKit;
using ThisIsATrap.Tests;

namespace ThisIsATrap.IntegrationTests;

/// <summary>Its own instance, because these tests change the global rule set and webhook list.</summary>
public sealed class RulesApp : TrapApp;

public sealed class ErrorSimulationTests(RulesApp app) : IClassFixture<RulesApp>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<SmtpCommandException> SendExpectingFailure(string to, string from = "sender@example.com")
    {
        using var client = await app.ConnectSmtpAsync();
        var message = TestMail.Build(subject: "should not arrive", to: to);
        return await Assert.ThrowsAsync<SmtpCommandException>(() =>
            client.SendAsync(FormatOptions.Default, new SendRequest(message, MailboxAddress.Parse(from), [MailboxAddress.Parse(to)]), Ct));
    }

    [Theory]
    [InlineData("someone@reject.test", 550, "5.1.1")]
    [InlineData("someone@full.test", 452, "4.2.2")]
    [InlineData("someone@tempfail.test", 451, "4.3.0")]
    public async Task Built_in_addresses_return_their_codes(string to, int code, string enhanced)
    {
        var ex = await SendExpectingFailure(to);
        Assert.Equal(code, (int)ex.StatusCode);
        Assert.Contains(enhanced, ex.Message);
        Assert.Contains("simulated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejections_are_counted_and_streamed()
    {
        using var http = app.CreateClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        await reader.ReadLineAsync(cts.Token);
        var before = (await app.GetJsonAsync("/api/status")).GetProperty("smtp").GetProperty("messagesRejected").GetInt64();

        _ = Task.Run(() => SendExpectingFailure("streamed@reject.test"), cts.Token);
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null || !line.StartsWith("data: ")) continue;
            using var evt = JsonDocument.Parse(line[6..]);
            if (evt.RootElement.GetProperty("type").GetString() != "rejected") continue;
            Assert.Equal("streamed@reject.test", evt.RootElement.GetProperty("payload").GetProperty("address").GetString());
            Assert.Equal(550, evt.RootElement.GetProperty("payload").GetProperty("code").GetInt32());
            break;
        }
        Assert.True((await app.GetJsonAsync("/api/status")).GetProperty("smtp").GetProperty("messagesRejected").GetInt64() > before);
    }

    [Fact]
    public async Task Rules_are_editable_through_the_api_including_sender_rules_and_delays()
    {
        using var http = app.CreateClient();
        var original = await app.GetJsonAsync("/api/settings/smtp-rules");
        try
        {
            var put = await http.PutAsJsonAsync("/api/settings/smtp-rules", new object[]
            {
                new { enabled = true, match = "from", pattern = "*@spammer.test", code = 553, enhancedCode = "5.7.1", message = "Go away" },
                new { enabled = true, match = "rcpt", pattern = "slowish@x.test", code = 250, enhancedCode = "2.1.5", message = "OK", delayMs = 600 },
            }, Ct);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var ex = await SendExpectingFailure("someone@example.com", from: "bot@spammer.test");
            Assert.Equal(553, (int)ex.StatusCode);

            // Default rules were replaced, so reject.test now delivers.
            await app.SendAsync(TestMail.Build(subject: "reject-test-now-ok", to: "someone@reject.test"));
            await app.FindAsync("reject-test-now-ok");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await app.SendAsync(TestMail.Build(subject: "slowish", to: "slowish@x.test"));
            Assert.True(sw.ElapsedMilliseconds >= 500, $"only {sw.ElapsedMilliseconds} ms");
            await app.FindAsync("slowish");
        }
        finally
        {
            await http.PutAsJsonAsync("/api/settings/smtp-rules", original, Ct);
        }
    }

    [Fact]
    public async Task Defaults_endpoint_and_limits()
    {
        using var http = app.CreateClient();
        var defaults = await app.GetJsonAsync("/api/settings/smtp-rules/defaults");
        Assert.Contains(defaults.EnumerateArray(), r => r.GetProperty("pattern").GetString() == "*@reject.test");

        var tooMany = Enumerable.Range(0, SmtpRules.MaxRules + 1).Select(i => new { pattern = $"u{i}@x.test" }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/settings/smtp-rules", tooMany, Ct)).StatusCode);
    }

    // ---------------------------------------------------------------- webhooks

    /// <summary>A one-shot HTTP receiver on loopback that captures the next request.</summary>
    private static (int Port, Task<string> Request) Receive(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var request = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                var stream = client.GetStream();
                var buffer = new byte[64 * 1024];
                var text = new StringBuilder();
                while (true)
                {
                    var n = await stream.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    var s = text.ToString();
                    var headerEnd = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd < 0) continue;
                    var lengthLine = s[..headerEnd].Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var length = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':')[1].Trim());
                    if (Encoding.UTF8.GetByteCount(s[(headerEnd + 4)..]) >= length) break;
                }
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
                return text.ToString();
            }
            finally { listener.Stop(); }
        }, ct);
        return (port, request);
    }

    [Fact]
    public async Task Webhook_receives_a_signed_notification_when_mail_arrives()
    {
        using var http = app.CreateClient();
        var (port, received) = Receive(Ct);
        var put = await http.PutAsJsonAsync("/api/settings/webhooks", new[] { new { url = $"http://127.0.0.1:{port}/hook", secret = "shh", enabled = true } }, Ct);
        var saved = await put.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(saved[0].GetProperty("hasSecret").GetBoolean());
        Assert.False(saved[0].TryGetProperty("secret", out _));

        await app.SendAsync(TestMail.Build(subject: "hooked"));
        var request = await received.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        var body = request[(request.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        Assert.StartsWith("POST /hook", request);
        Assert.Contains($"{WebhookService.SignatureHeader}: {WebhookService.Sign("shh", body)}", request);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("hooked", json.RootElement.GetProperty("message").GetProperty("subject").GetString());

        // The delivery result is shown afterwards.
        for (var i = 0; i < 50; i++)
        {
            var views = await app.GetJsonAsync("/api/settings/webhooks");
            if (views[0].GetProperty("lastDelivery").ValueKind == JsonValueKind.Object)
            {
                Assert.True(views[0].GetProperty("lastDelivery").GetProperty("ok").GetBoolean());
                break;
            }
            await Task.Delay(50, Ct);
        }
        await http.PutAsJsonAsync("/api/settings/webhooks", Array.Empty<object>(), Ct);
    }

    [Fact]
    public async Task Webhook_test_endpoint_and_validation()
    {
        using var http = app.CreateClient();
        var (port, received) = Receive(Ct);
        var saved = await (await http.PutAsJsonAsync("/api/settings/webhooks", new[] { new { url = $"http://127.0.0.1:{port}/test" } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = saved[0].GetProperty("id").GetString();

        var result = await (await http.PostAsync($"/api/settings/webhooks/{id}/test", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Contains("webhook.test", await received.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/settings/webhooks/nope/test", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsJsonAsync("/api/settings/webhooks", new[] { new { url = "ftp://x" } }, Ct)).StatusCode);

        // Cloud metadata receivers are refused even though private networks are allowed for webhooks.
        var meta = await (await http.PutAsJsonAsync("/api/settings/webhooks", new[] { new { url = "http://169.254.169.254/hook" } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var metaResult = await (await http.PostAsync($"/api/settings/webhooks/{meta[0].GetProperty("id").GetString()}/test", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.False(metaResult.GetProperty("ok").GetBoolean());
        Assert.Contains("metadata", metaResult.GetProperty("error").GetString());

        await http.PutAsJsonAsync("/api/settings/webhooks", Array.Empty<object>(), Ct);
    }
}
