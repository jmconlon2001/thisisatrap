using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;
using ThisIsATrap.Analysis;

namespace ThisIsATrap.Tests;

public sealed class LinkCheckerTests
{
    /// <summary>Answers requests from a route table; records every request.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add($"{request.Method} {request.RequestUri}");
            return respond(request, ct);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string? location = null, string? contentType = null)
    {
        var r = new HttpResponseMessage(code) { Content = new ByteArrayContent([]) };
        if (location is not null) r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        if (contentType is not null) r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return r;
    }

    private static (LinkChecker Checker, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, LinkCheckOptions? options = null)
    {
        var handler = new FakeHandler((r, _) => Task.FromResult(respond(r)));
        return (new LinkChecker(new Factory(handler), options ?? new LinkCheckOptions()), handler);
    }

    private static MimeMessage Mail(string html, string? text = null, IEnumerable<(string, byte[])>? inline = null) =>
        TestMail.Build(html: html, text: text, inlineImages: inline);

    private static LinkResult One(LinkReport r, string url) => r.Links.Single(l => l.Url == url);

    [Fact]
    public void Inspect_classifies_without_fetching()
    {
        var (checker, handler) = Create(_ => throw new InvalidOperationException("must not fetch"));
        var m = Mail("<a href=\"https://ok.example/a\">A</a><a href=\"mailto:x@y.z\">mail</a><a href=\"#top\">top</a>" +
                     "<a href=\"/relative\">rel</a><a href=\"http://localhost:5000/reset\">reset</a>" +
                     "<img src=\"cid:logo\" alt=\"logo\"><img src=\"cid:gone\" alt=\"gone\">",
                     inline: [("logo", TestMail.OnePixelPng)]);

        var r = checker.Inspect(m);

        Assert.False(r.Checked);
        Assert.Empty(handler.Requests);
        Assert.Equal("unchecked", One(r, "https://ok.example/a").Status);
        Assert.Equal("skipped", One(r, "mailto:x@y.z").Status);
        Assert.Equal("skipped", One(r, "#top").Status);
        Assert.Equal("error", One(r, "/relative").Status);
        Assert.Contains("relative-url", One(r, "/relative").Issues);
        var local = One(r, "http://localhost:5000/reset");
        Assert.Equal("blocked", local.Status);
        Assert.Contains("local-address", local.Issues);
        Assert.Contains("insecure-http", local.Issues);
        Assert.Equal("ok", One(r, "cid:logo").Status);
        Assert.Equal("missing", One(r, "cid:gone").Status);
    }

    [Fact]
    public async Task Ok_broken_and_issue_detection()
    {
        var (checker, _) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/ok" => Status(HttpStatusCode.OK),
            "/gone" => Status(HttpStatusCode.NotFound),
            "/img.png" => Status(HttpStatusCode.OK, contentType: "text/html"),
            _ => Status(HttpStatusCode.InternalServerError),
        });
        var m = Mail("<a href=\"https://a.example/ok\">www.other.com</a><a href=\"https://a.example/gone\">gone</a>" +
                     "<img src=\"https://a.example/img.png\" alt=\"x\"><a href=\"https://bit.ly/ok\">short</a>");

        var r = await checker.CheckAsync(m, TestContext.Current.CancellationToken);

        Assert.True(r.Checked);
        var ok = One(r, "https://a.example/ok");
        Assert.Equal("ok", ok.Status);
        Assert.Equal(200, ok.HttpStatus);
        Assert.Contains("text-mismatch", ok.Issues);
        var gone = One(r, "https://a.example/gone");
        Assert.Equal("broken", gone.Status);
        Assert.Equal("HTTP 404 Not Found", gone.Error);
        Assert.Contains("not-an-image", One(r, "https://a.example/img.png").Issues);
        Assert.Contains("url-shortener", One(r, "https://bit.ly/ok").Issues);
        Assert.Equal(1, r.Broken);
        Assert.Equal(3, r.Ok);          // /ok, the image and bit.ly/ok
        Assert.Equal(4, r.Total);
        Assert.Equal(3, r.Warnings);    // text-mismatch, not-an-image, url-shortener
    }

    [Fact]
    public async Task Redirects_are_followed_and_recorded()
    {
        var (checker, _) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/start" => Status(HttpStatusCode.MovedPermanently, "/middle"),
            "/middle" => Status(HttpStatusCode.Found, "http://b.example/end"),
            "/end" => Status(HttpStatusCode.OK),
            _ => Status(HttpStatusCode.NotFound),
        });

        var r = await checker.CheckAsync(Mail("<a href=\"https://a.example/start\">x</a>"), TestContext.Current.CancellationToken);
        var link = r.Links.Single();

        Assert.Equal("redirect", link.Status);
        Assert.Equal(["https://a.example/middle", "http://b.example/end"], link.Redirects);
        Assert.Equal("http://b.example/end", link.FinalUrl);
        Assert.Contains("downgrade-to-http", link.Issues);
    }

    [Fact]
    public async Task Redirect_loops_stop_after_five_hops()
    {
        var (checker, handler) = Create(r => Status(HttpStatusCode.Found, r.RequestUri!.AbsolutePath == "/a" ? "/b" : "/a"));
        var link = (await checker.CheckAsync(Mail("<a href=\"https://loop.example/a\">x</a>"), TestContext.Current.CancellationToken)).Links.Single();

        Assert.Equal("error", link.Status);
        Assert.Equal("Too many redirects", link.Error);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task Head_rejected_falls_back_to_get()
    {
        var (checker, handler) = Create(r => r.Method == HttpMethod.Head ? Status(HttpStatusCode.MethodNotAllowed) : Status(HttpStatusCode.OK));
        var link = (await checker.CheckAsync(Mail("<a href=\"https://a.example/x\">x</a>"), TestContext.Current.CancellationToken)).Links.Single();

        Assert.Equal("ok", link.Status);
        Assert.Equal(["HEAD https://a.example/x", "GET https://a.example/x"], handler.Requests);
    }

    [Fact]
    public async Task Slow_servers_time_out()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Status(HttpStatusCode.OK); });
        var checker = new LinkChecker(new Factory(handler), new LinkCheckOptions { TimeoutSeconds = 1 });

        var link = (await checker.CheckAsync(Mail("<a href=\"https://slow.example/\">x</a>"), TestContext.Current.CancellationToken)).Links.Single();

        Assert.Equal("timeout", link.Status);
    }

    [Fact]
    public async Task Policy_refusals_and_network_errors_are_reported()
    {
        var (checker, _) = Create(r => r.RequestUri!.Host == "blocked.example"
            ? throw new HttpRequestException("connect failed", new OutboundBlockedException("10.0.0.1 is on a private network"))
            : throw new HttpRequestException("Name or service not known"));

        var r = await checker.CheckAsync(Mail("<a href=\"https://blocked.example/\">a</a><a href=\"https://nxdomain.example/\">b</a>"), TestContext.Current.CancellationToken);

        Assert.Equal("blocked", One(r, "https://blocked.example/").Status);
        Assert.Contains("private network", One(r, "https://blocked.example/").Error);
        Assert.Equal("error", One(r, "https://nxdomain.example/").Status);
        Assert.Equal(2, r.Broken);
    }

    [Fact]
    public async Task Urls_are_deduplicated_and_capped()
    {
        var (checker, handler) = Create(_ => Status(HttpStatusCode.OK));
        var html = string.Concat(Enumerable.Range(0, 10).Select(i => $"<a href=\"https://a.example/{i}\">x</a><a href=\"https://a.example/{i}\">dup</a>"));

        var r = await checker.CheckAsync(Mail(html), TestContext.Current.CancellationToken);
        Assert.Equal(10, r.Total);

        var (capped, _) = Create(_ => Status(HttpStatusCode.OK), new LinkCheckOptions { MaxUrls = 3 });
        Assert.Equal(3, (await capped.CheckAsync(Mail(html), TestContext.Current.CancellationToken)).Total);
        Assert.Equal(10, handler.Requests.Count);
    }

    [Fact]
    public async Task Private_hosts_are_fetched_only_when_allowed()
    {
        var (checker, handler) = Create(_ => Status(HttpStatusCode.OK), new LinkCheckOptions { AllowPrivateNetworks = true });
        var link = (await checker.CheckAsync(Mail("<a href=\"http://localhost:5000/x\">x</a>"), TestContext.Current.CancellationToken)).Links.Single();

        Assert.Equal("ok", link.Status);
        Assert.Contains("local-address", link.Issues);   // still flagged: recipients can't open it
        Assert.Single(handler.Requests);
    }
}

public sealed class OutboundPolicyTests
{
    [Theory]
    [InlineData("169.254.169.254", true, "metadata")]
    [InlineData("169.254.169.254", false, "metadata")]
    [InlineData("fd00:ec2::254", true, "metadata")]
    [InlineData("127.0.0.1", false, "private")]
    [InlineData("10.1.2.3", false, "private")]
    [InlineData("192.168.1.1", false, "private")]
    [InlineData("::1", false, "private")]
    [InlineData("0.0.0.0", true, "routable")]
    [InlineData("224.0.0.1", true, "multicast")]
    [InlineData("ff02::1", true, "multicast")]
    public void Rejects(string ip, bool allowPrivate, string reason) =>
        Assert.Contains(reason, OutboundPolicy.Reject(IPAddress.Parse(ip), allowPrivate));

    [Theory]
    [InlineData("93.184.216.34", false)]
    [InlineData("2606:2800:220:1::1", false)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("::ffff:192.168.0.1", true)]
    public void Allows(string ip, bool allowPrivate) => Assert.Null(OutboundPolicy.Reject(IPAddress.Parse(ip), allowPrivate));

    /// <summary>A minimal HTTP server on loopback that answers 204 to anything.</summary>
    private static (TcpListener Listener, Task Server) Serve(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                _ = await stream.ReadAsync(buffer, ct);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"), ct);
            }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }, ct);
        return (listener, server);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task Real_handler_refuses_loopback_when_private_networks_are_not_allowed(string host)
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, _) = Serve(ct);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var http = new HttpClient(OutboundPolicy.CreateHandler(allowPrivate: false));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://{host}:{port}/", ct));

        Assert.IsType<OutboundBlockedException>(ex.InnerException, exactMatch: false);
        listener.Stop();
    }

    [Fact]
    public async Task Real_handler_connects_when_private_networks_are_allowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, server) = Serve(ct);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var http = new HttpClient(OutboundPolicy.CreateHandler(allowPrivate: true));

        using var response = await http.GetAsync($"http://127.0.0.1:{port}/", ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await server;
        listener.Stop();
    }

    [Fact]
    public async Task Metadata_endpoint_is_refused_even_when_private_networks_are_allowed()
    {
        using var http = new HttpClient(OutboundPolicy.CreateHandler(allowPrivate: true));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://169.254.169.254/latest/meta-data/", TestContext.Current.CancellationToken));
        Assert.Contains("metadata", ex.InnerException!.Message);
    }

    [Fact]
    public void Handler_never_follows_redirects_or_sends_cookies()
    {
        using var handler = OutboundPolicy.CreateHandler(false);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
    }
}
