using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ThisIsATrap.Tests;

namespace ThisIsATrap.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class HealthEndpointTests(TrapApp app)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Healthz_is_a_plain_liveness_probe()
    {
        using var http = app.CreateClient();
        var response = await http.GetAsync("/healthz", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Readyz_reports_each_dependency()
    {
        using var http = app.CreateClient();
        var response = await http.GetAsync("/readyz", Ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = doc.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.NotEqual("Unhealthy", root.GetProperty("status").GetString());
        var checks = root.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c);
        Assert.Equal(["database", "smtp", "storage"], checks.Keys.Order());
        Assert.Equal("Healthy", checks["smtp"].GetProperty("status").GetString());
        Assert.Equal("Healthy", checks["database"].GetProperty("status").GetString());
        Assert.Equal(app.DataPath, checks["storage"].GetProperty("data").GetProperty("dataPath").GetString());
    }

    [Fact]
    public async Task Status_api_describes_the_whole_system()
    {
        var s = await app.GetJsonAsync("/api/status");

        Assert.Equal("ThisIsATrap", s.GetProperty("app").GetProperty("name").GetString());
        Assert.StartsWith("1.", s.GetProperty("app").GetProperty("version").GetString());
        Assert.Equal("Testing", s.GetProperty("app").GetProperty("environment").GetString());
        Assert.True(s.GetProperty("app").GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.True(s.GetProperty("smtp").GetProperty("listening").GetBoolean());
        Assert.Equal(app.SmtpPort, s.GetProperty("smtp").GetProperty("listenPort").GetInt32());
        Assert.Equal(app.DataPath, s.GetProperty("storage").GetProperty("dataPath").GetString());
        Assert.True(s.GetProperty("storage").GetProperty("databaseBytes").GetInt64() > 0);
        Assert.True(s.GetProperty("storage").GetProperty("diskTotalBytes").GetInt64() > 0);
        Assert.Equal(3, s.GetProperty("health").GetProperty("checks").GetArrayLength());
        Assert.True(s.GetProperty("purge").TryGetProperty("intervalMinutes", out _));
        Assert.True(s.GetProperty("events").GetProperty("connectedClients").GetInt32() >= 0);
    }

    [Fact]
    public async Task Status_counts_received_messages()
    {
        var before = (await app.GetJsonAsync("/api/status")).GetProperty("smtp").GetProperty("messagesReceived").GetInt64();
        var subject = "count-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject));
        await app.FindAsync(subject);

        var smtp = (await app.GetJsonAsync("/api/status")).GetProperty("smtp");

        Assert.Equal(before + 1, smtp.GetProperty("messagesReceived").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, smtp.GetProperty("lastMessageUtc").ValueKind);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/js/app.js")]
    [InlineData("/css/app.css")]
    public async Task Static_files_are_revalidated_so_upgrades_take_effect(string path)
    {
        using var http = app.CreateClient();
        var response = await http.GetAsync(path, Ct);

        Assert.True(response.Headers.CacheControl?.NoCache, $"{path} should be sent with Cache-Control: no-cache");
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task Web_client_has_the_status_view()
    {
        using var http = app.CreateClient();
        var html = await http.GetStringAsync("/", Ct);
        Assert.Contains("id=\"status-view\"", html);
        Assert.Contains("data-view=\"status\"", html);
    }
}

/// <summary>An instance whose SMTP port is already taken by another process.</summary>
public sealed class BlockedSmtpApp : TrapApp
{
    private TcpListener? _blocker;

    public override async ValueTask InitializeAsync()
    {
        _blocker = new TcpListener(IPAddress.Any, SmtpPort);
        _blocker.Start();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _blocker?.Stop();
        _blocker?.Dispose();
    }
}

public sealed class NotReadyTests(BlockedSmtpApp app) : IClassFixture<BlockedSmtpApp>
{
    [Fact]
    public async Task Readyz_is_503_while_smtp_cannot_listen_but_healthz_stays_up()
    {
        using var http = app.CreateClient();
        HttpResponseMessage? ready = null;
        for (var i = 0; i < 50; i++)
        {
            ready = await http.GetAsync("/readyz", TestContext.Current.CancellationToken);
            if (ready.StatusCode == HttpStatusCode.ServiceUnavailable) break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready!.StatusCode);
        var body = await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"Unhealthy\"", body);
        Assert.Contains("error", body);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/healthz", TestContext.Current.CancellationToken)).StatusCode);

        var status = await app.GetJsonAsync("/api/status");
        Assert.False(status.GetProperty("smtp").GetProperty("listening").GetBoolean());
        Assert.Equal("Unhealthy", status.GetProperty("health").GetProperty("status").GetString());
    }
}
