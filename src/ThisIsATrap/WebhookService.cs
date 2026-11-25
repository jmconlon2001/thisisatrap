using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ThisIsATrap;

/// <summary>A configured webhook. <see cref="Secret"/> is write-only: the API never returns it.</summary>
public sealed record Webhook
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string Url { get; init; } = "";
    /// <summary>Only fire for this inbox slug; null = every inbox.</summary>
    public string? Inbox { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Secret { get; init; }
}

public sealed record WebhookStatus(DateTimeOffset AttemptedUtc, bool Ok, int? HttpStatus, string? Error, int Attempts);

/// <summary>How a webhook is shown by the API: the secret replaced by HasSecret, plus the last delivery result.</summary>
public sealed record WebhookView(string Id, string Url, string? Inbox, bool Enabled, bool HasSecret, WebhookStatus? LastDelivery);

/// <summary>What a client sends to PUT /api/settings/webhooks. Secret: null = keep, "" = remove, text = set.</summary>
public sealed record WebhookInput(string? Id, string? Url, string? Inbox, bool Enabled = true, string? Secret = null);

public sealed class WebhookOptions
{
    /// <summary>Delays between delivery attempts. Three attempts in total by default.</summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)];
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Public base URL of this instance (e.g. http://trap:8025/) so payloads can include absolute links.</summary>
    public string? PublicUrl { get; set; }
}

/// <summary>
/// Posts a JSON notification to the configured webhooks whenever mail arrives.
/// Outbound HTTP only (never mail); private-network receivers are allowed, metadata addresses are not.
/// </summary>
public sealed class WebhookService(MailStore store, IHttpClientFactory http, WebhookOptions options, TimeProvider time, ILogger<WebhookService> log) : BackgroundService
{
    public const string SettingKey = "webhooks";
    public const string HttpClientName = "webhooks";
    public const string SignatureHeader = "X-Trap-Signature";
    public const int MaxWebhooks = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<(Webhook Hook, string Body, string Event)> _queue =
        Channel.CreateBounded<(Webhook, string, string)>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentDictionary<string, WebhookStatus> _status = new();

    public IReadOnlyList<Webhook> Get() => store.GetSetting<List<Webhook>>(SettingKey) ?? [];

    public IReadOnlyList<WebhookView> Views() => Get().Select(View).ToList();

    public WebhookView View(Webhook h) => new(h.Id, h.Url, h.Inbox, h.Enabled, !string.IsNullOrEmpty(h.Secret), _status.GetValueOrDefault(h.Id));

    /// <summary>Replaces the webhook list. Existing secrets are kept unless explicitly changed. Throws on invalid URLs.</summary>
    public IReadOnlyList<WebhookView> Save(IEnumerable<WebhookInput> input)
    {
        var existing = Get().ToDictionary(h => h.Id);
        var hooks = new List<Webhook>();
        foreach (var i in input.Take(MaxWebhooks))
        {
            var url = (i.Url ?? "").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException($"Webhook URL must be an absolute http(s) URL: '{url}'");
            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Put credentials in the secret, not in the webhook URL.");
            var previous = i.Id is not null && existing.TryGetValue(i.Id, out var p) ? p : null;
            var secret = i.Secret switch
            {
                null => previous?.Secret,
                "" => null,
                var s => s.Length > 256 ? s[..256] : s,
            };
            hooks.Add(new Webhook
            {
                Id = previous?.Id ?? Guid.NewGuid().ToString("N")[..12],
                Url = url,
                Inbox = string.IsNullOrWhiteSpace(i.Inbox) ? null : i.Inbox.Trim(),
                Enabled = i.Enabled,
                Secret = secret,
            });
        }
        store.SaveSetting(SettingKey, hooks);
        foreach (var gone in existing.Keys.Except(hooks.Select(h => h.Id))) _status.TryRemove(gone, out _);
        return hooks.Select(View).ToList();
    }

    /// <summary>Queues a "message.received" notification for every matching, enabled webhook.</summary>
    public void Notify(MessageSummary message, Inbox inbox)
    {
        var hooks = Get().Where(h => h.Enabled && (h.Inbox is null || h.Inbox == inbox.Slug)).ToList();
        if (hooks.Count == 0) return;
        var body = Payload("message.received", message, inbox);
        foreach (var hook in hooks) _queue.Writer.TryWrite((hook, body, "message.received"));
    }

    /// <summary>Sends a sample payload to one webhook right away (one attempt) and returns the result.</summary>
    public async Task<WebhookStatus?> TestAsync(string id, CancellationToken ct)
    {
        var hook = Get().FirstOrDefault(h => h.Id == id);
        if (hook is null) return null;
        var sample = new MessageSummary(new string('0', 32), time.GetUtcNow(), "\"ThisIsATrap\" <test@thisisatrap.test>", "you@example.com",
            "Webhook test from ThisIsATrap", "This is a test notification.", 0, 0, false, Inbox.DefaultSlug);
        var body = Payload("webhook.test", sample, new Inbox(Inbox.DefaultId, Inbox.DefaultSlug, Inbox.DefaultName, time.GetUtcNow(), null));
        return await DeliverAsync(hook, body, "webhook.test", attempts: 1, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (hook, body, evt) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await DeliverAsync(hook, body, evt, attempts: options.RetryDelays.Length + 1, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogError(ex, "Webhook {Id} delivery crashed", hook.Id); }
        }
    }

    private async Task<WebhookStatus> DeliverAsync(Webhook hook, string body, string evt, int attempts, CancellationToken ct)
    {
        WebhookStatus status = new(time.GetUtcNow(), false, null, "not sent", 0);
        var client = http.CreateClient(HttpClientName);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Trap-Event", evt);
            request.Headers.Add("X-Trap-Delivery", Guid.NewGuid().ToString("N"));
            if (!string.IsNullOrEmpty(hook.Secret)) request.Headers.Add(SignatureHeader, Sign(hook.Secret, body));
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(options.Timeout);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var ok = response.IsSuccessStatusCode;
                status = new(time.GetUtcNow(), ok, (int)response.StatusCode, ok ? null : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", attempt);
                if (ok || (int)response.StatusCode is >= 400 and < 500 and not 408 and not 429) break;   // don't retry client errors
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                status = new(time.GetUtcNow(), false, null, $"No response within {options.Timeout.TotalSeconds:0}s", attempt);
            }
            catch (HttpRequestException ex)
            {
                status = new(time.GetUtcNow(), false, null, ex.InnerException?.Message ?? ex.Message, attempt);
            }
            if (attempt < attempts) await Task.Delay(options.RetryDelays[Math.Min(attempt - 1, options.RetryDelays.Length - 1)], time, ct);
        }
        _status[hook.Id] = status;
        if (!status.Ok) log.LogWarning("Webhook {Id} to {Url} failed after {Attempts} attempt(s): {Error}", hook.Id, hook.Url, status.Attempts, status.Error);
        return status;
    }

    /// <summary>"sha256=" + hex HMAC-SHA256 of the exact request body, keyed with the webhook secret.</summary>
    public static string Sign(string secret, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    private string Payload(string evt, MessageSummary m, Inbox inbox)
    {
        var baseUrl = string.IsNullOrWhiteSpace(options.PublicUrl) ? "/" : options.PublicUrl.TrimEnd('/') + "/";
        return JsonSerializer.Serialize(new
        {
            @event = evt,
            sentUtc = time.GetUtcNow(),
            inbox = new { slug = inbox.Slug, name = inbox.Name },
            message = m,
            urls = new
            {
                ui = $"{baseUrl}#{m.Id}",
                api = $"{baseUrl}api/messages/{m.Id}",
                raw = $"{baseUrl}api/messages/{m.Id}/raw",
            },
        }, Json);
    }
}
