using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ThisIsATrap.Tests;

namespace ThisIsATrap.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class LinkApiTests(TrapApp app)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Links_are_listed_without_fetching_and_private_ones_refused_by_default()
    {
        var subject = "links-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject,
            html: "<a href=\"http://localhost:1/reset\">reset</a><a href=\"http://169.254.169.254/latest\">meta</a><a href=\"mailto:a@b.c\">mail</a>"));
        var id = (await app.FindAsync(subject)).GetProperty("id").GetString();
        using var http = app.CreateClient();

        var listed = await app.GetJsonAsync($"/api/messages/{id}/links");
        var checkedReport = await (await http.PostAsync($"/api/messages/{id}/links/check", null, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.False(listed.GetProperty("checked").GetBoolean());
        Assert.Equal(3, listed.GetProperty("total").GetInt32());
        Assert.True(checkedReport.GetProperty("checked").GetBoolean());
        var byUrl = checkedReport.GetProperty("links").EnumerateArray().ToDictionary(l => l.GetProperty("url").GetString()!, l => l.GetProperty("status").GetString());
        Assert.Equal("blocked", byUrl["http://localhost:1/reset"]);
        Assert.Equal("blocked", byUrl["http://169.254.169.254/latest"]);
        Assert.Equal("skipped", byUrl["mailto:a@b.c"]);
    }

    [Fact]
    public async Task Unknown_message_is_404()
    {
        using var http = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/messages/{Guid.NewGuid():N}/links", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/messages/{Guid.NewGuid():N}/links/check", null, Ct)).StatusCode);
    }
}

/// <summary>An instance allowed to check private-network links, plus a tiny local web server to check against.</summary>
public sealed class LocalLinksApp : TrapApp
{
    private readonly TcpListener _web = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public int WebPort => ((IPEndPoint)_web.LocalEndpoint).Port;

    public LocalLinksApp()
    {
        _web.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var client = await _web.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var buffer = new byte[4096];
                            var n = await stream.ReadAsync(buffer, _stop.Token);
                            var path = Encoding.ASCII.GetString(buffer, 0, n).Split(' ')[1];
                            var response = path switch
                            {
                                "/ok" => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n",
                                "/old" => "HTTP/1.1 301 Moved Permanently\r\nLocation: /ok\r\nContent-Length: 0\r\n",
                                _ => "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n",
                            };
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "Connection: close\r\n\r\n"), _stop.Token);
                        }
                    });
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
            }
        });
    }

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?> { ["ThisIsATrap:LinkCheck:AllowPrivateNetworks"] = "true" };

    public override async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _web.Stop();
        _web.Dispose();
        await base.DisposeAsync();
    }
}

public sealed class LocalLinkCheckTests(LocalLinksApp app) : IClassFixture<LocalLinksApp>
{
    [Fact]
    public async Task Checks_real_http_responses_and_redirects()
    {
        var b = $"http://127.0.0.1:{app.WebPort}";
        await app.SendAsync(TestMail.Build(subject: "real-links", html: $"<a href=\"{b}/ok\">ok</a><a href=\"{b}/old\">old</a><a href=\"{b}/missing\">missing</a>"));
        var id = (await app.FindAsync("real-links")).GetProperty("id").GetString();
        using var http = app.CreateClient();

        var r = await (await http.PostAsync($"/api/messages/{id}/links/check", null, TestContext.Current.CancellationToken)).Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var links = r.GetProperty("links").EnumerateArray().ToDictionary(l => l.GetProperty("url").GetString()!);

        Assert.Equal("ok", links[$"{b}/ok"].GetProperty("status").GetString());
        Assert.Equal("redirect", links[$"{b}/old"].GetProperty("status").GetString());
        Assert.Equal($"{b}/ok", links[$"{b}/old"].GetProperty("finalUrl").GetString());
        Assert.Equal("broken", links[$"{b}/missing"].GetProperty("status").GetString());
        Assert.Equal(404, links[$"{b}/missing"].GetProperty("httpStatus").GetInt32());
        Assert.Equal(1, r.GetProperty("broken").GetInt32());
    }
}
