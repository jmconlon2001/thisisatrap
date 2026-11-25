using ThisIsATrap;
using MimeKit;

namespace ThisIsATrap.Tests;

public sealed class MessageParserTests
{
    private static MessageRecord RecordFor(MimeMessage m, string envelopeTo) => new(
        Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, MessageParser.Addresses(m.From), MessageParser.Addresses(m.To),
        MessageParser.Addresses(m.Cc), m.Subject ?? "", "", 100, MessageParser.CountAttachments(m), false, "s@example.com", envelopeTo, "127.0.0.1");

    [Fact]
    public void Preview_prefers_text_and_collapses_whitespace()
    {
        var m = TestMail.Build(text: "  Hello\r\n\r\n   world  ", html: "<p>ignored</p>");
        Assert.Equal("Hello world", MessageParser.Preview(m));
    }

    [Fact]
    public void Preview_falls_back_to_html_without_style_or_script()
    {
        var m = TestMail.Build(text: null, html: "<style>p{color:red}</style><script>evil()</script><p>Hi &amp; welcome</p>");
        Assert.Equal("Hi & welcome", MessageParser.Preview(m));
    }

    [Fact]
    public void Preview_is_truncated_to_220_chars()
    {
        var m = TestMail.Build(text: new string('x', 500));
        Assert.Equal(220, MessageParser.Preview(m).Length);
    }

    [Fact]
    public void Preview_of_empty_message_is_empty() =>
        Assert.Equal("", MessageParser.Preview(TestMail.Build(text: null, html: null)));

    [Fact]
    public void Attachments_are_counted_but_bodies_are_not()
    {
        var m = TestMail.Build(html: "<img src=\"cid:logo\">", attachments: [TestMail.Csv(), TestMail.Pdf()],
            inlineImages: [("logo", TestMail.OnePixelPng)]);

        Assert.Equal(3, MessageParser.CountAttachments(m)); // 2 files + 1 inline image
        Assert.Equal(0, MessageParser.CountAttachments(TestMail.Build(html: "<p>x</p>")));
    }

    [Fact]
    public void ToDetail_lists_attachments_with_sizes_inline_flag_and_index()
    {
        var csv = TestMail.Csv();
        var m = TestMail.Build(html: "<img src=\"cid:logo\">", attachments: [csv], inlineImages: [("logo", TestMail.OnePixelPng)]);

        var d = MessageParser.ToDetail(RecordFor(m, "user@example.com"), m);

        var file = Assert.Single(d.Attachments, a => a.FileName == "report.csv");
        Assert.Equal(csv.Item3.Length, file.Size);
        Assert.False(file.Inline);
        var logo = Assert.Single(d.Attachments, a => a.ContentId == "logo");
        Assert.True(logo.Inline);
        Assert.Equal(TestMail.OnePixelPng.Length, logo.Size);
        Assert.Same(MessageParser.Leaves(m)[file.Index], MessageParser.Leaves(m).Single(e => e is MimePart { FileName: "report.csv" }));
        Assert.True(d.HasHtml);
        Assert.Equal("Plain body", d.Text);
    }

    [Fact]
    public void ToDetail_derives_bcc_from_envelope_case_insensitively()
    {
        var m = TestMail.Build(to: "user@example.com", cc: "cc@example.com");
        var d = MessageParser.ToDetail(RecordFor(m, "USER@example.com, cc@example.com, secret@example.com"), m);
        Assert.Equal("secret@example.com", d.Bcc);
    }

    [Fact]
    public void ToDetail_has_no_bcc_when_envelope_matches_headers()
    {
        var m = TestMail.Build(to: "user@example.com");
        Assert.Equal("", MessageParser.ToDetail(RecordFor(m, "user@example.com"), m).Bcc);
    }

    [Fact]
    public void ToDetail_exposes_headers_and_date()
    {
        var m = TestMail.Build(headers: new Dictionary<string, string> { ["X-Custom"] = "42" });
        var d = MessageParser.ToDetail(RecordFor(m, "user@example.com"), m);

        Assert.Contains(d.Headers, h => h.Name == "X-Custom" && h.Value == "42");
        Assert.Equal(m.Date, d.Date);
        Assert.Equal(m.MessageId, d.MessageId);
    }

    [Fact]
    public void ToDetail_date_is_null_when_header_missing()
    {
        var m = TestMail.Build();
        m.Headers.Remove(HeaderId.Date);
        Assert.Null(MessageParser.ToDetail(RecordFor(m, "user@example.com"), m).Date);
    }

    [Fact]
    public void RenderHtml_injects_base_target_into_existing_head()
    {
        var html = MessageParser.RenderHtml("id", TestMail.Build(html: "<html><head><title>t</title></head><body>b</body></html>"));
        Assert.StartsWith("<html><head><base target=\"_blank\">", html);
    }

    [Fact]
    public void RenderHtml_prepends_base_target_when_there_is_no_head()
    {
        var html = MessageParser.RenderHtml("id", TestMail.Build(html: "<p>no head</p>"));
        Assert.StartsWith("<base target=\"_blank\">", html);
        Assert.EndsWith("<p>no head</p>", html);
    }

    [Fact]
    public void RenderHtml_rewrites_cid_references_to_relative_urls()
    {
        var html = MessageParser.RenderHtml("id", TestMail.Build(html: "<img src=\"cid:logo@x\"><div style=\"background:url(cid:bg)\">"));
        Assert.Contains("src=\"cid/logo%40x\"", html);
        Assert.Contains("url(cid/bg)", html);
        Assert.DoesNotContain("cid:", html);
    }

    [Fact]
    public void RenderHtml_of_text_only_message_is_just_the_head()
    {
        var html = MessageParser.RenderHtml("id", TestMail.Build(html: null));
        Assert.StartsWith("<base", html);
    }

    [Theory]
    [InlineData("logo")]
    [InlineData("<logo>")]
    [InlineData("LOGO")]
    public void FindByContentId_ignores_brackets_and_case(string cid)
    {
        var m = TestMail.Build(html: "<img src=\"cid:logo\">", inlineImages: [("logo", TestMail.OnePixelPng)]);
        Assert.NotNull(MessageParser.FindByContentId(m, cid));
    }

    [Fact]
    public void FindByContentId_returns_null_for_unknown() =>
        Assert.Null(MessageParser.FindByContentId(TestMail.Build(), "missing"));

    [Fact]
    public async Task WriteContentAsync_decodes_attachment_bytes()
    {
        var pdf = TestMail.Pdf();
        var m = TestMail.Build(attachments: [pdf]);
        var part = MessageParser.Leaves(m).Single(e => e is MimePart { FileName: "invoice.pdf" });

        using var output = new MemoryStream();
        await MessageParser.WriteContentAsync(part, output, CancellationToken.None);

        Assert.Equal(pdf.Item3, output.ToArray());
    }

    [Fact]
    public async Task Attached_message_is_an_attachment_named_after_its_subject()
    {
        var inner = TestMail.Build(subject: "Inner: re/fwd?");
        var outer = TestMail.Build();
        var mixed = new Multipart("mixed") { outer.Body!, new MessagePart { Message = inner } };
        outer.Body = mixed;

        var leaf = MessageParser.Leaves(outer).OfType<MessagePart>().Single();
        Assert.True(MessageParser.IsAttachmentLike(leaf));
        var name = MessageParser.FileNameOf(leaf, 0);
        Assert.EndsWith(".eml", name);
        Assert.DoesNotContain('/', name);

        using var output = new MemoryStream();
        await MessageParser.WriteContentAsync(leaf, output, CancellationToken.None);
        Assert.Contains("Inner: re/fwd?", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void Unnamed_part_gets_a_generated_name_from_its_type()
    {
        var part = new MimePart("image", "png") { Content = new MimeContent(new MemoryStream(TestMail.OnePixelPng)) };
        Assert.Equal("part-3.png", MessageParser.FileNameOf(part, 2));

        var unknown = new MimePart("application", "x-made-up") { Content = new MimeContent(new MemoryStream([1])) };
        Assert.Equal("part-1.bin", MessageParser.FileNameOf(unknown, 0));
    }

    [Fact]
    public async Task LoadAsync_parses_fixture_with_legacy_charset()
    {
        var m = await MessageParser.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "latin1.eml"), CancellationToken.None);
        Assert.Equal("Café crème", m.Subject);
        Assert.Contains("Grüße", m.TextBody);
    }

    [Fact]
    public async Task LoadAsync_tolerates_malformed_mime()
    {
        var m = await MessageParser.LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "malformed.eml"), CancellationToken.None);
        Assert.Equal("Broken boundary", m.Subject);
        Assert.NotNull(MessageParser.Preview(m));
        _ = MessageParser.CountAttachments(m);
    }
}
