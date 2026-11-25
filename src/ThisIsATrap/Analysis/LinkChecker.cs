using System.Diagnostics;
using System.Net;
using MimeKit;

namespace ThisIsATrap.Analysis;

public sealed class LinkCheckOptions
{
    /// <summary>Allow checking links on localhost / private networks. Off by default (they are flagged instead).</summary>
    public bool AllowPrivateNetworks { get; set; }
    public int TimeoutSeconds { get; set; } = 8;
    public int MaxUrls { get; set; } = 150;
    public int Concurrency { get; set; } = 8;
}

/// <summary>
/// Result for one URL. Status: ok, redirect (ok after redirects), broken (4xx/5xx), error, timeout,
/// blocked (refused by the outbound policy), skipped (not http), missing (cid: with no matching part).
/// </summary>
public sealed record LinkResult(
    string Url, string Kind, string Text, string Status, int? HttpStatus, string? FinalUrl,
    IReadOnlyList<string> Redirects, long? ElapsedMs, IReadOnlyList<string> Issues, string? Error);

public sealed record LinkReport(bool Checked, int Total, int Ok, int Broken, int Warnings, IReadOnlyList<LinkResult> Links);

/// <summary>Lists (and optionally fetches) every link and image in a message.</summary>
public sealed class LinkChecker(IHttpClientFactory httpFactory, LinkCheckOptions options)
{
    public const string HttpClientName = "linkcheck";
    private static readonly string[] Shorteners = ["bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at", "rb.gy", "tiny.cc"];

    /// <summary>Static review only: no network. Each link gets its issues; status is "unchecked" for http(s).</summary>
    public LinkReport Inspect(MimeMessage message)
    {
        var results = Distinct(message).Select(l => Static(message, l) ?? Result(l, "unchecked")).ToList();
        return Summarize(false, results);
    }

    /// <summary>Fetches every http(s) URL (HEAD, falling back to GET), following up to 5 redirects.</summary>
    public async Task<LinkReport> CheckAsync(MimeMessage message, CancellationToken ct)
    {
        var links = Distinct(message);
        using var gate = new SemaphoreSlim(Math.Max(1, options.Concurrency));
        var tasks = links.Select(async l =>
        {
            if (Static(message, l) is { } fixedResult) return fixedResult;
            await gate.WaitAsync(ct);
            try { return await FetchAsync(l, ct); }
            finally { gate.Release(); }
        });
        return Summarize(true, await Task.WhenAll(tasks));
    }

    private List<LinkRef> Distinct(MimeMessage message) =>
        LinkExtractor.Extract(message.HtmlBody, message.TextBody)
            .GroupBy(l => l.Url, StringComparer.Ordinal).Select(g => g.First())
            .Take(Math.Max(1, options.MaxUrls))
            .ToList();

    /// <summary>A result that needs no network access (non-http, cid, or blocked by policy), else null.</summary>
    private LinkResult? Static(MimeMessage message, LinkRef l)
    {
        if (l.Url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            return Result(l, MessageParser.FindByContentId(message, l.Url[4..]) is null ? "missing" : "ok", issues: [],
                error: MessageParser.FindByContentId(message, l.Url[4..]) is null ? "No attachment with this Content-ID" : null);
        if (l.Url.StartsWith('#')) return Result(l, "skipped", error: "In-page anchor");
        if (!Uri.TryCreate(l.Url, UriKind.Absolute, out var uri))
            return Result(l, "error", issues: ["relative-url"], error: "Not an absolute URL; relative links don't work in email");
        if (uri.Scheme is not ("http" or "https"))
            return Result(l, "skipped", error: $"{uri.Scheme}: links aren't fetched");
        if (!options.AllowPrivateNetworks && HostClassifier.IsLocalOrPrivate(uri.IdnHost))
            return Result(l, "blocked", issues: Issues(l, uri), error: $"{uri.IdnHost} is a local/private address; it won't work for recipients");
        return null;
    }

    private async Task<LinkResult> FetchAsync(LinkRef l, CancellationToken ct)
    {
        var uri = new Uri(l.Url);
        var issues = Issues(l, uri);
        var redirects = new List<string>();
        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
        var http = httpFactory.CreateClient(HttpClientName);
        try
        {
            var current = uri;
            for (var hop = 0; ; hop++)
            {
                using var response = await SendAsync(http, current, timeout.Token);
                var code = (int)response.StatusCode;
                if (code is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= 5) return Result(l, "error", code, current.ToString(), redirects, sw, issues, "Too many redirects");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    redirects.Add(current.ToString());
                    if (uri.Scheme == "https" && current.Scheme == "http") issues.Add("downgrade-to-http");
                    continue;
                }
                var status = code is >= 200 and < 400 ? (redirects.Count > 0 ? "redirect" : "ok") : "broken";
                if (l.Kind is "image" or "background" && status != "broken" && response.Content.Headers.ContentType?.MediaType is { } type && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    issues.Add("not-an-image");
                return Result(l, status, code, current.ToString(), redirects, sw, issues, status == "broken" ? $"HTTP {code} {response.ReasonPhrase}" : null);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result(l, "timeout", null, null, redirects, sw, issues, $"No response within {options.TimeoutSeconds}s");
        }
        catch (HttpRequestException ex) when (FindBlocked(ex) is { } blocked)
        {
            return Result(l, "blocked", null, null, redirects, sw, issues, blocked.Message);
        }
        catch (HttpRequestException ex)
        {
            return Result(l, "error", null, null, redirects, sw, issues, ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>HEAD first; some servers reject it, so fall back to GET (headers only, body never read).</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), HttpCompletionOption.ResponseHeadersRead, ct);
        if (head.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden or HttpStatusCode.NotImplemented or HttpStatusCode.BadRequest))
            return head;
        head.Dispose();
        return await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static List<string> Issues(LinkRef l, Uri uri)
    {
        var issues = new List<string>();
        if (uri.Scheme == "http") issues.Add("insecure-http");
        if (HostClassifier.IsLocalOrPrivate(uri.IdnHost)) issues.Add("local-address");
        if (Shorteners.Contains(uri.IdnHost.ToLowerInvariant())) issues.Add("url-shortener");
        if (l.Kind == "link" && LinkExtractor.HostInText(l.Text) is { } shown && !SpamCheck.SameOrgDomain(shown, uri.IdnHost))
            issues.Add("text-mismatch");
        return issues;
    }

    private static OutboundBlockedException? FindBlocked(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is OutboundBlockedException b) return b;
        return null;
    }

    private static LinkResult Result(LinkRef l, string status, IReadOnlyList<string>? issues = null, string? error = null) =>
        new(l.Url, l.Kind, l.Text, status, null, null, [], null, issues ?? [], error);

    private static LinkResult Result(LinkRef l, string status, int? code, string? finalUrl, List<string> redirects, Stopwatch sw, List<string> issues, string? error) =>
        new(l.Url, l.Kind, l.Text, status, code, finalUrl, redirects, sw.ElapsedMilliseconds, issues.Distinct().ToList(), error);

    private static LinkReport Summarize(bool isChecked, IReadOnlyList<LinkResult> results) => new(
        isChecked,
        results.Count,
        results.Count(r => r.Status is "ok" or "redirect"),
        results.Count(r => r.Status is "broken" or "error" or "timeout" or "missing" or "blocked"),
        results.Count(r => r.Issues.Count > 0),
        results);
}
