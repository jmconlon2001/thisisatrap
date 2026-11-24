using System.Net;
using System.Text.RegularExpressions;
using MimeKit;

namespace MailTrap;

public sealed record AttachmentInfo(int Index, string FileName, string ContentType, long Size, bool Inline, string? ContentId);

public sealed record HeaderInfo(string Name, string Value);

public sealed record MessageDetail(
    string Id,
    DateTimeOffset ReceivedUtc,
    DateTimeOffset? Date,
    string From,
    string To,
    string Cc,
    string Bcc,
    string ReplyTo,
    string Subject,
    string? MessageId,
    long Size,
    bool IsRead,
    string EnvelopeFrom,
    string EnvelopeTo,
    string ClientIp,
    bool HasHtml,
    string? Html,
    string? Text,
    IReadOnlyList<AttachmentInfo> Attachments,
    IReadOnlyList<HeaderInfo> Headers);

public static partial class MessageParser
{
    public static async Task<MimeMessage> LoadAsync(string path, CancellationToken ct) =>
        await MimeMessage.LoadAsync(path, ct);

    /// <summary>Every leaf entity in the message, in a stable order; the position is the attachment index used in URLs.</summary>
    public static List<MimeEntity> Leaves(MimeMessage m) => m.BodyParts.ToList();

    /// <summary>True for files and embedded resources; false for the text/html bodies themselves.</summary>
    public static bool IsAttachmentLike(MimeEntity e) => e switch
    {
        MessagePart => true,
        MimePart { IsAttachment: true } => true,
        TextPart { FileName: null } => false,
        MimePart => true,
        _ => false,
    };

    public static string FileNameOf(MimeEntity e, int index) => e switch
    {
        MimePart { FileName: { Length: > 0 } name } => name,
        MessagePart mp => (mp.Message?.Subject is { Length: > 0 } s ? Sanitize(s) : "attached-message") + ".eml",
        _ => $"part-{index + 1}{GuessExtension(e.ContentType.MimeType)}",
    };

    public static int CountAttachments(MimeMessage m) => m.BodyParts.Count(IsAttachmentLike);

    public static string Preview(MimeMessage m)
    {
        var text = m.TextBody;
        if (string.IsNullOrWhiteSpace(text) && m.HtmlBody is { } html)
            text = WebUtility.HtmlDecode(Tags().Replace(StyleScript().Replace(html, " "), " "));
        text = Whitespace().Replace(text ?? "", " ").Trim();
        return text.Length > 220 ? text[..220] : text;
    }

    public static string Addresses(InternetAddressList list) => string.Join(", ", list.Select(a => a.ToString()));

    public static MessageDetail ToDetail(MessageRecord r, MimeMessage m)
    {
        var leaves = Leaves(m);
        var attachments = new List<AttachmentInfo>();
        for (var i = 0; i < leaves.Count; i++)
        {
            var e = leaves[i];
            if (!IsAttachmentLike(e)) continue;
            attachments.Add(new AttachmentInfo(
                i, FileNameOf(e, i), e.ContentType.MimeType, DecodedSize(e),
                !e.IsAttachment && e.ContentId is not null, e.ContentId));
        }

        // Envelope recipients that don't appear in To/Cc were Bcc'd.
        var visible = m.To.Mailboxes.Concat(m.Cc.Mailboxes).Select(x => x.Address.ToLowerInvariant()).ToHashSet();
        var bcc = r.EnvelopeTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => !visible.Contains(a.ToLowerInvariant()));

        return new MessageDetail(
            r.Id, r.ReceivedUtc, m.Date == DateTimeOffset.MinValue ? null : m.Date,
            r.From, r.To, r.Cc, string.Join(", ", bcc), Addresses(m.ReplyTo), r.Subject, m.MessageId,
            r.Size, r.IsRead, r.EnvelopeFrom, r.EnvelopeTo, r.ClientIp,
            m.HtmlBody is not null, m.HtmlBody, m.TextBody, attachments,
            m.Headers.Select(h => new HeaderInfo(h.Field, h.Value)).ToList());
    }

    /// <summary>HTML body prepared for display in a sandboxed iframe: cid: images point at our endpoint and links open in a new tab.</summary>
    public static string RenderHtml(string id, MimeMessage m)
    {
        var html = m.HtmlBody ?? "";
        // Relative to .../api/messages/{id}/html so it also works when hosted under an IIS sub-path.
        html = CidRef().Replace(html, match => $"cid/{Uri.EscapeDataString(match.Groups[1].Value)}");
        const string head = "<base target=\"_blank\"><meta name=\"color-scheme\" content=\"light\"><style>html{background:#fff}</style>";
        var headTag = HeadTag().Match(html);
        return headTag.Success ? html.Insert(headTag.Index + headTag.Length, head) : head + html;
    }

    public static MimeEntity? FindByContentId(MimeMessage m, string cid)
    {
        cid = cid.Trim('<', '>');
        return m.BodyParts.FirstOrDefault(e => string.Equals(e.ContentId?.Trim('<', '>'), cid, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task WriteContentAsync(MimeEntity e, Stream output, CancellationToken ct)
    {
        switch (e)
        {
            case MimePart { Content: not null } p: await p.Content.DecodeToAsync(output, ct); break;
            case MessagePart { Message: not null } mp: await mp.Message.WriteToAsync(output, ct); break;
        }
    }

    private static long DecodedSize(MimeEntity e)
    {
        var counter = new CountingStream();
        switch (e)
        {
            case MimePart { Content: not null } p: p.Content.DecodeTo(counter); break;
            case MessagePart { Message: not null } mp: mp.Message.WriteTo(counter); break;
        }
        return counter.Length;
    }

    private static string GuessExtension(string mimeType) => MimeTypes.TryGetExtension(mimeType, out var ext) ? ext : ".bin";

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length > 80 ? clean[..80] : clean;
    }

    [GeneratedRegex(@"cid:([^""'\s)>]+)", RegexOptions.IgnoreCase)] private static partial Regex CidRef();
    [GeneratedRegex(@"<head[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex HeadTag();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"<(style|script)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex StyleScript();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
