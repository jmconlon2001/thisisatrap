using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;
using ThisIsATrap.Analysis;

namespace ThisIsATrap.Tests;

public sealed class SpamCheckTests
{
    /// <summary>Runs the rules on the message as the server sees it: serialized, then parsed back.</summary>
    private static IReadOnlyList<SpamRuleHit> Run(MimeMessage m, string envelopeFrom = "sender@example.com", long size = 1000)
    {
        using var raw = new MemoryStream();
        m.WriteTo(raw);
        raw.Position = 0;
        return SpamCheck.Run(MimeMessage.Load(raw), envelopeFrom, size);
    }

    private static HashSet<string> Ids(MimeMessage m, string envelopeFrom = "sender@example.com", long size = 1000) =>
        Run(m, envelopeFrom, size).Select(h => h.Id).ToHashSet();

    /// <summary>A well-formed message that should trigger nothing.</summary>
    private static MimeMessage Clean() => TestMail.Build(
        subject: "Your weekly report",
        text: "Here is your weekly report. Everything looks good this week and there is nothing to do.",
        html: "<p>Here is your weekly report. Everything looks good this week and there is nothing to do.</p><p><a href=\"https://example.com/report\">Open the report</a></p>");

    [Fact]
    public void Clean_message_has_no_hits()
    {
        Assert.Empty(Run(Clean()).Select(h => h.Id));
        Assert.Equal("Looks clean", SpamCheck.Verdict(0));
    }

    [Fact]
    public void Html_only_and_empty_bodies()
    {
        Assert.Contains("MIME_HTML_ONLY", Ids(TestMail.Build(text: null, html: "<p>Hello there, this is the body of the message.</p>")));
        Assert.Contains("EMPTY_MESSAGE", Ids(TestMail.Build(text: null, html: null)));
    }

    [Fact]
    public void Missing_headers()
    {
        var m = Clean();
        m.Headers.Remove(HeaderId.Date);
        m.Headers.Remove(HeaderId.MessageId);
        var ids = Ids(m);
        Assert.Contains("MISSING_DATE", ids);
        Assert.Contains("MISSING_MID", ids);
    }

    [Fact]
    public void Multipart_without_mime_version()
    {
        // Built as raw text, because MimeKit always writes a MIME-Version header itself.
        var raw = "From: a@example.com\r\nTo: b@example.com\r\nSubject: Hi there\r\nDate: Tue, 24 Nov 2026 12:00:00 +0000\r\nMessage-ID: <x@example.com>\r\n" +
                  "Content-Type: multipart/mixed; boundary=\"b\"\r\n\r\n--b\r\nContent-Type: text/plain\r\n\r\nHello there, this is the body.\r\n--b--\r\n";
        var m = MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw)), TestContext.Current.CancellationToken);
        Assert.Contains("MISSING_MIMEOLE", SpamCheck.Run(m, "a@example.com", raw.Length).Select(h => h.Id));
    }

    [Fact]
    public void Missing_from()
    {
        var m = Clean();
        m.From.Clear();
        Assert.Contains("NO_FROM", Ids(m));
    }

    [Theory]
    [InlineData("", "MISSING_SUBJECT")]
    [InlineData("FREE MONEY FOR EVERYONE", "SUBJ_ALL_CAPS")]
    [InlineData("Act fast!!!", "SUBJ_EXCESS_PUNCT")]
    [InlineData("Win $$$ today", "SUBJ_EXCESS_PUNCT")]
    public void Subject_rules(string subject, string rule)
    {
        var m = Clean();
        m.Subject = subject;
        Assert.Contains(rule, Ids(m));
    }

    [Fact]
    public void Short_all_caps_subject_is_fine()
    {
        var m = Clean();
        m.Subject = "FYI: API";
        Assert.DoesNotContain("SUBJ_ALL_CAPS", Ids(m));
    }

    [Fact]
    public void Spam_phrases_are_scored_and_capped()
    {
        var m = TestMail.Build(text: "Congratulations winner! Act now, it's 100% free and risk-free with a cash bonus and free gift. Urgent!");
        var hit = Run(m).Single(h => h.Id == "SPAMMY_PHRASES");
        Assert.Equal(2.5, hit.Score);
        Assert.Contains("congratulations", hit.Detail);
    }

    [Fact]
    public void From_envelope_and_reply_to_domain_mismatch()
    {
        var m = Clean();
        m.ReplyTo.Add(MailboxAddress.Parse("support@other.net"));
        var ids = Ids(m, envelopeFrom: "bounce@mailer.othercorp.com");
        Assert.Contains("FROM_ENVELOPE_MISMATCH", ids);
        Assert.Contains("REPLYTO_DIFF_DOMAIN", ids);

        // A subdomain of the same organisation is aligned.
        Assert.DoesNotContain("FROM_ENVELOPE_MISMATCH", Ids(Clean(), envelopeFrom: "bounces@mail.example.com"));
        Assert.DoesNotContain("FROM_ENVELOPE_MISMATCH", Ids(Clean(), envelopeFrom: ""));
    }

    [Fact]
    public void Image_heavy_html_and_missing_alt()
    {
        var ids = Ids(TestMail.Build(html: "<img src=\"https://cdn.example.com/a.png\"><img src=\"https://cdn.example.com/b.png\" alt=\"b\">"));
        Assert.Contains("HTML_IMAGE_RATIO", ids);
        Assert.Contains("HTML_IMG_NO_ALT", ids);
    }

    [Fact]
    public void Hidden_text()
    {
        var html = "<div style=\"display:none;max-height:0\">This is a hidden preheader with quite a lot of extra text</div><p>Body text</p>";
        Assert.Contains("HTML_HIDDEN_TEXT", Ids(TestMail.Build(html: html)));
    }

    [Fact]
    public void Gmail_clipping()
    {
        var html = "<p>" + new string('x', 110 * 1024) + "</p>";
        Assert.Contains("HTML_CLIPPED_GMAIL", Ids(TestMail.Build(html: html)));
    }

    [Fact]
    public void Unsubscribe_link_without_header_and_one_click()
    {
        var html = Clean().HtmlBody + "<a href=\"https://example.com/unsub\">Unsubscribe</a>";
        Assert.Contains("NO_LIST_UNSUBSCRIBE", Ids(TestMail.Build(html: html)));

        var withHeader = TestMail.Build(html: html, headers: new Dictionary<string, string> { ["List-Unsubscribe"] = "<https://example.com/unsub>" });
        var ids = Ids(withHeader);
        Assert.DoesNotContain("NO_LIST_UNSUBSCRIBE", ids);
        Assert.Contains("NO_ONE_CLICK_UNSUB", ids);
    }

    [Fact]
    public void Large_message() => Assert.Contains("LARGE_MESSAGE", Ids(Clean(), size: 11L * 1024 * 1024));

    [Fact]
    public void Url_shortener()
    {
        var hit = Run(TestMail.Build(html: Clean().HtmlBody + "<a href=\"https://bit.ly/abc\">Read more</a>")).Single(h => h.Id == "URL_SHORTENER");
        Assert.Equal("bit.ly", hit.Detail);
    }

    [Fact]
    public void Link_text_showing_a_different_domain_is_phishing()
    {
        var html = Clean().HtmlBody + "<a href=\"https://evil.example.net/login\">www.mybank.com</a>";
        var hit = Run(TestMail.Build(html: html)).Single(h => h.Id == "LINK_TEXT_MISMATCH");
        Assert.Contains("evil.example.net", hit.Detail);

        var same = Clean().HtmlBody + "<a href=\"https://www.mybank.com/login\">mybank.com</a>";
        Assert.DoesNotContain("LINK_TEXT_MISMATCH", Ids(TestMail.Build(html: same)));
    }

    [Theory]
    [InlineData("http://localhost:5000/reset")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData("https://192.168.1.20/x")]
    [InlineData("https://myapp.local/x")]
    [InlineData("https://10.0.0.5/x")]
    public void Development_urls_are_flagged(string url)
    {
        var ids = Ids(TestMail.Build(text: "Reset here: " + url + " thanks, the team"));
        Assert.Contains("LOCAL_URL", ids);
    }

    [Fact]
    public void Plain_http_links()
    {
        Assert.Contains("HTTP_LINKS", Ids(TestMail.Build(html: Clean().HtmlBody + "<a href=\"http://example.com\">x</a>")));
    }

    [Fact]
    public void Risky_attachments()
    {
        var m = TestMail.Build(attachments: [("setup.exe", "application/octet-stream", [1, 2]), ("budget.xlsm", "application/vnd.ms-excel", [1])]);
        var hit = Run(m).Single(h => h.Id == "RISKY_ATTACHMENT");
        Assert.Equal(3.0, hit.Score);
        Assert.Contains("setup.exe", hit.Detail);
        Assert.DoesNotContain("RISKY_ATTACHMENT", Ids(TestMail.Build(attachments: [TestMail.Pdf()])));
    }

    [Fact]
    public void Hits_are_sorted_by_score()
    {
        var m = TestMail.Build(subject: "", text: null, html: "<img src=\"a.png\">", attachments: [("x.exe", "application/octet-stream", [1])]);
        var hits = Run(m);
        Assert.Equal(hits.OrderByDescending(h => h.Score).Select(h => h.Score), hits.Select(h => h.Score));
    }

    [Theory]
    [InlineData(0, "Looks clean")]
    [InlineData(1.9, "Looks clean")]
    [InlineData(2, "Some issues")]
    [InlineData(4.9, "Some issues")]
    [InlineData(5, "Likely spam")]
    public void Verdicts(double score, string verdict) => Assert.Equal(verdict, SpamCheck.Verdict(score));

    [Theory]
    [InlineData("example.com", "mail.example.com", true)]
    [InlineData("a.b.example.com", "example.com", true)]
    [InlineData("example.com", "example.net", false)]
    public void Organisational_domain_comparison(string a, string b, bool same) => Assert.Equal(same, SpamCheck.SameOrgDomain(a, b));
}

public sealed class SpamAssassinTests
{
    private const string Report = "SPAMD/1.1 0 EX_OK\r\nContent-length: 300\r\nSpam: True ; 7.3 / 5.0\r\n\r\n" +
        "Content analysis details:   (7.3 points, 5.0 required)\r\n\r\n" +
        " pts rule name              description\r\n" +
        "---- ---------------------- --------------------------------------------------\r\n" +
        " 1.4 MISSING_DATE           Missing Date: header\r\n" +
        " 5.0 GTUBE                  BODY: Generic Test for Unsolicited Bulk Email\r\n" +
        "-0.1 DKIM_VALID             Message has at least one valid DKIM signature\r\n" +
        " 1.0 MIME_HTML_ONLY         BODY: Message only has text/html MIME parts\r\n";

    [Fact]
    public void Parses_score_threshold_verdict_and_rules()
    {
        var r = SpamAssassinClient.Parse(Report);

        Assert.True(r.Available);
        Assert.True(r.IsSpam);
        Assert.Equal(7.3, r.Score);
        Assert.Equal(5.0, r.Threshold);
        Assert.Equal(4, r.Rules.Count);
        Assert.Equal("GTUBE", r.Rules[0].Id);
        Assert.Equal(-0.1, r.Rules[^1].Score);
        Assert.Equal("Missing Date: header", r.Rules.Single(x => x.Id == "MISSING_DATE").Description);
    }

    [Theory]
    [InlineData("SPAMD/1.1 76 EX_PROTOCOL\r\n\r\n")]
    [InlineData("garbage")]
    [InlineData("SPAMD/1.1 0 EX_OK\r\n\r\nno spam header")]
    public void Bad_responses_are_reported_not_thrown(string response)
    {
        var r = SpamAssassinClient.Parse(response);
        Assert.False(r.Available);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Talks_spamc_protocol_to_a_spamd()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? request = null;
        var ct = TestContext.Current.CancellationToken;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            request = await reader.ReadToEndAsync(ct);   // client half-closes after sending
            await stream.WriteAsync(Encoding.ASCII.GetBytes(Report), ct);
        }, ct);

        var message = Encoding.ASCII.GetBytes("Subject: hi\r\n\r\nbody\r\n");
        var result = await new SpamAssassinClient(new SpamAssassinOptions { Host = "127.0.0.1", Port = port })
            .CheckAsync(message, TestContext.Current.CancellationToken);
        await server;

        Assert.True(result.Available);
        Assert.Equal(7.3, result.Score);
        Assert.StartsWith("REPORT SPAMC/1.5\r\nContent-length: " + message.Length, request);
        Assert.EndsWith("Subject: hi\r\n\r\nbody\r\n", request);
    }

    [Fact]
    public async Task Unreachable_spamd_is_reported_not_thrown()
    {
        var result = await new SpamAssassinClient(new SpamAssassinOptions { Host = "127.0.0.1", Port = 1, TimeoutSeconds = 2 })
            .CheckAsync([1], TestContext.Current.CancellationToken);
        Assert.False(result.Available);
        Assert.Contains("unavailable", result.Error);
    }

    [Fact]
    public void Disabled_without_a_host()
    {
        Assert.False(new SpamAssassinClient(new SpamAssassinOptions()).Enabled);
        Assert.True(new SpamAssassinClient(new SpamAssassinOptions { Host = "spamd" }).Enabled);
    }
}

public sealed class LinkExtractorTests
{
    [Fact]
    public void Finds_links_images_backgrounds_and_text_urls()
    {
        var html = "<a href=\"https://a.example/x\"> Click  here </a><img src=\"https://cdn.example/i.png\" alt=\"logo\">" +
                   "<table><tr><td background=\"https://cdn.example/bg.jpg\">x</td></tr></table>" +
                   "<div style=\"background-image:url('https://cdn.example/s.png')\">y</div><style>.h{background:url(https://cdn.example/c.png)}</style>";
        var links = LinkExtractor.Extract(html, "See https://text.example/page. Thanks");

        Assert.Contains(links, l => l is { Kind: "link", Url: "https://a.example/x", Text: "Click here" });
        Assert.Contains(links, l => l is { Kind: "image", Url: "https://cdn.example/i.png", Text: "logo" });
        Assert.Contains(links, l => l is { Kind: "background", Url: "https://cdn.example/bg.jpg" });
        Assert.Contains(links, l => l is { Kind: "background", Url: "https://cdn.example/s.png" });
        Assert.Contains(links, l => l is { Kind: "background", Url: "https://cdn.example/c.png" });
        Assert.Contains(links, l => l is { Kind: "text", Url: "https://text.example/page" });
    }

    [Fact]
    public void Text_urls_already_in_html_are_not_duplicated()
    {
        var links = LinkExtractor.Extract("<a href=\"https://a.example/x\">x</a>", "Go to https://a.example/x");
        Assert.Single(links);
    }

    [Theory]
    [InlineData("https://Example.COM/a", "example.com")]
    [InlineData("http://x.test:8080", "x.test")]
    [InlineData("mailto:a@b.c", null)]
    [InlineData("/relative", null)]
    [InlineData("cid:logo", null)]
    public void Http_host(string url, string? host) => Assert.Equal(host, LinkExtractor.HttpHost(url));

    [Theory]
    [InlineData("www.mybank.com", "www.mybank.com")]
    [InlineData("https://secure.bank.com/login", "secure.bank.com")]
    [InlineData("Click here", null)]
    [InlineData("v1.2", null)]
    public void Host_in_link_text(string text, string? host) => Assert.Equal(host, LinkExtractor.HostInText(text));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("api.localhost", true)]
    [InlineData("printer.local", true)]
    [InlineData("db.internal", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("fd12::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("example.com", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:4860::8888", false)]
    public void Local_and_private_hosts(string host, bool expected) => Assert.Equal(expected, HostClassifier.IsLocalOrPrivate(host));

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("fd00:ec2::254", true)]
    [InlineData("10.0.0.1", false)]
    public void Metadata_addresses(string ip, bool expected) => Assert.Equal(expected, HostClassifier.IsMetadata(IPAddress.Parse(ip)));
}
