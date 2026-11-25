using ThisIsATrap;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ThisIsATrap.Tests;

/// <summary>A unique temp folder that is deleted when disposed.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "thisisatrap-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        // SQLite pooled connections can hold the file briefly; clear them first.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Minimal host environment so MailStore can resolve relative paths.</summary>
public sealed class TestEnv(string root) : IWebHostEnvironment
{
    public string WebRootPath { get; set; } = root;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ApplicationName { get; set; } = "ThisIsATrap";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Test";
}

/// <summary>Settable clock.</summary>
public sealed class FakeTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public static class TestStore
{
    public static MailStore Create(TempDir dir, TimeProvider? time = null, Action<TrapOptions>? configure = null)
    {
        var options = new TrapOptions { DataPath = dir.Path };
        configure?.Invoke(options);
        return new MailStore(Options.Create(options), new TestEnv(dir.Path), NullLogger<MailStore>.Instance, time);
    }

    /// <summary>Stores a message built by <see cref="TestMail"/> and returns its record.</summary>
    public static async Task<MessageRecord> AddAsync(this MailStore store, MimeMessage message, DateTimeOffset? received = null,
        string? envelopeTo = null, string envelopeFrom = "sender@example.com", string clientIp = "127.0.0.1")
    {
        using var raw = new MemoryStream();
        await message.WriteToAsync(raw);
        raw.Position = 0;
        var record = new MessageRecord(
            Guid.NewGuid().ToString("N"),
            received ?? DateTimeOffset.UtcNow,
            MessageParser.Addresses(message.From),
            MessageParser.Addresses(message.To),
            MessageParser.Addresses(message.Cc),
            message.Subject ?? "",
            MessageParser.Preview(message),
            raw.Length,
            MessageParser.CountAttachments(message),
            false,
            envelopeFrom,
            envelopeTo ?? string.Join(", ", message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(m => m.Address)),
            clientIp);
        await store.AddAsync(record, raw, CancellationToken.None);
        return record;
    }
}

/// <summary>Builds realistic MIME messages for tests.</summary>
public static class TestMail
{
    public static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public static MimeMessage Build(
        string subject = "Hello",
        string from = "Sender <sender@example.com>",
        string to = "user@example.com",
        string? cc = null,
        string? text = "Plain body",
        string? html = null,
        IEnumerable<(string Name, string ContentType, byte[] Data)>? attachments = null,
        IEnumerable<(string ContentId, byte[] Data)>? inlineImages = null,
        IDictionary<string, string>? headers = null)
    {
        var m = new MimeMessage();
        m.From.Add(InternetAddress.Parse(from));
        foreach (var a in to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) m.To.Add(InternetAddress.Parse(a));
        if (cc is not null)
            foreach (var a in cc.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) m.Cc.Add(InternetAddress.Parse(a));
        m.Subject = subject;
        m.Date = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        m.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("example.com");
        if (headers is not null) foreach (var (k, v) in headers) m.Headers.Add(k, v);

        var body = new BodyBuilder { TextBody = text, HtmlBody = html };
        if (inlineImages is not null)
            foreach (var (cid, data) in inlineImages)
            {
                var img = body.LinkedResources.Add(cid + ".png", data, ContentType.Parse("image/png"));
                img.ContentId = cid;
            }
        if (attachments is not null)
            foreach (var (name, type, data) in attachments) body.Attachments.Add(name, data, ContentType.Parse(type));
        m.Body = body.ToMessageBody();
        return m;
    }

    public static (string, string, byte[]) Csv(string name = "report.csv") =>
        (name, "text/csv", "id,total\r\n1,10\r\n"u8.ToArray());

    public static (string, string, byte[]) Pdf(string name = "invoice.pdf") =>
        (name, "application/pdf", "%PDF-1.4\n%%EOF"u8.ToArray());
}
