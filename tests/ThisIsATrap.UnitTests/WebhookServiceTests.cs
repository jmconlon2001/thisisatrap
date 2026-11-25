using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace ThisIsATrap.Tests;

public sealed class WebhookServiceTests : IDisposable
{
    private sealed record Captured(string Url, string Body, Dictionary<string, string> Headers);

    private sealed class Receiver(Func<int, HttpStatusCode> status) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Requests)
            {
                Requests.Add(new Captured(request.RequestUri!.ToString(), body, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
                return new HttpResponseMessage(status(Requests.Count));
            }
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private readonly TempDir _dir = new();
    private readonly MailStore _store;

    public WebhookServiceTests() => _store = TestStore.Create(_dir);

    public void Dispose() => _dir.Dispose();

    private WebhookService Create(Receiver receiver) => new(_store, new Factory(receiver),
        new WebhookOptions { RetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)], PublicUrl = "http://trap:8025/" },
        TimeProvider.System, NullLogger<WebhookService>.Instance);

    private static MessageSummary Summary(string inbox = Inbox.DefaultSlug) =>
        new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "a@example.com", "b@example.com", "Hello", "preview", 10, 0, false, inbox);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    [Fact]
    public void Signature_is_hex_hmac_sha256_of_the_body()
    {
        // Well-known HMAC-SHA256 test vector.
        Assert.Equal("sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            WebhookService.Sign("key", "The quick brown fox jumps over the lazy dog"));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://x.example/hook")]
    [InlineData("/relative")]
    [InlineData("https://user:pass@x.example/hook")]
    public void Invalid_urls_are_rejected(string url) =>
        Assert.Throws<ArgumentException>(() => Create(new Receiver(_ => HttpStatusCode.OK)).Save([new WebhookInput(null, url, null)]));

    [Fact]
    public void Secrets_are_write_only_and_kept_unless_changed()
    {
        var service = Create(new Receiver(_ => HttpStatusCode.OK));

        var created = service.Save([new WebhookInput(null, "https://x.example/hook", null, true, "s1")]).Single();
        Assert.True(created.HasSecret);
        Assert.DoesNotContain("s1", JsonSerializer.Serialize(service.Views()));

        service.Save([new WebhookInput(created.Id, "https://x.example/hook2", null, true, null)]);   // null = keep
        Assert.Equal("s1", service.Get().Single().Secret);
        Assert.Equal(created.Id, service.Get().Single().Id);

        service.Save([new WebhookInput(created.Id, "https://x.example/hook2", null, true, "s2")]);   // text = replace
        Assert.Equal("s2", service.Get().Single().Secret);

        service.Save([new WebhookInput(created.Id, "https://x.example/hook2", null, true, "")]);     // "" = remove
        Assert.Null(service.Get().Single().Secret);
        Assert.False(service.Views().Single().HasSecret);
    }

    [Fact]
    public void List_is_capped()
    {
        var saved = Create(new Receiver(_ => HttpStatusCode.OK)).Save(Enumerable.Range(0, 30).Select(i => new WebhookInput(null, $"https://x.example/{i}", null)));
        Assert.Equal(WebhookService.MaxWebhooks, saved.Count);
    }

    [Fact]
    public async Task Delivers_signed_payload_to_matching_enabled_hooks()
    {
        var receiver = new Receiver(_ => HttpStatusCode.OK);
        var service = Create(receiver);
        service.Save([
            new WebhookInput(null, "https://all.example/hook", null, true, "topsecret"),
            new WebhookInput(null, "https://billing.example/hook", "billing", true),
            new WebhookInput(null, "https://off.example/hook", null, false),
        ]);
        await service.StartAsync(CancellationToken.None);

        var message = Summary();
        service.Notify(message, new Inbox(1, "default", "Default", DateTimeOffset.UtcNow, null));
        await WaitFor(() => service.Views().Any(v => v.LastDelivery is not null));
        await service.StopAsync(CancellationToken.None);

        var request = Assert.Single(receiver.Requests);   // billing-only and disabled hooks don't fire
        Assert.Equal("https://all.example/hook", request.Url);
        Assert.Equal(WebhookService.Sign("topsecret", request.Body), request.Headers[WebhookService.SignatureHeader]);
        Assert.Equal("message.received", request.Headers["X-Trap-Event"]);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("message.received", json.RootElement.GetProperty("event").GetString());
        Assert.Equal(message.Id, json.RootElement.GetProperty("message").GetProperty("id").GetString());
        Assert.Equal("default", json.RootElement.GetProperty("inbox").GetProperty("slug").GetString());
        Assert.Equal($"http://trap:8025/api/messages/{message.Id}", json.RootElement.GetProperty("urls").GetProperty("api").GetString());
        Assert.True(service.Views().Single(v => v.Url == "https://all.example/hook").LastDelivery!.Ok);
    }

    [Fact]
    public async Task Server_errors_are_retried_then_succeed()
    {
        var receiver = new Receiver(n => n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent);
        var service = Create(receiver);
        var hook = service.Save([new WebhookInput(null, "https://flaky.example/hook", null)]).Single();
        await service.StartAsync(CancellationToken.None);

        service.Notify(Summary(), new Inbox(1, "default", "Default", DateTimeOffset.UtcNow, null));
        await WaitFor(() => service.Views().Single().LastDelivery is not null);
        await service.StopAsync(CancellationToken.None);

        var status = service.Views().Single(v => v.Id == hook.Id).LastDelivery!;
        Assert.True(status.Ok);
        Assert.Equal(3, status.Attempts);
        Assert.Equal(204, status.HttpStatus);
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        var receiver = new Receiver(_ => HttpStatusCode.NotFound);
        var service = Create(receiver);
        service.Save([new WebhookInput(null, "https://gone.example/hook", null)]);

        var hook = service.Get().Single();
        var status = await service.TestAsync(hook.Id, TestContext.Current.CancellationToken);

        Assert.False(status!.Ok);
        Assert.Equal(404, status.HttpStatus);
        Assert.Single(receiver.Requests);
        Assert.Contains("webhook.test", receiver.Requests[0].Body);
        Assert.Null(await service.TestAsync("missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Network_failures_are_recorded()
    {
        var service = new WebhookService(_store, new Factory(new ThrowingHandler()), new WebhookOptions { RetryDelays = [] }, TimeProvider.System, NullLogger<WebhookService>.Instance);
        service.Save([new WebhookInput(null, "https://down.example/hook", null)]);

        var status = await service.TestAsync(service.Get().Single().Id, TestContext.Current.CancellationToken);

        Assert.False(status!.Ok);
        Assert.Contains("refused", status.Error);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }
}
