using MailKit;
using MailKit.Net.Smtp;
using ThisIsATrap.Tests;
using MimeKit;

namespace ThisIsATrap.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class SmtpTests(TrapApp app)
{
    [Fact]
    public async Task Ehlo_advertises_a_normal_smtp_server()
    {
        using var client = await app.ConnectSmtpAsync();

        Assert.True(client.Capabilities.Contains(SmtpCapability.Size));
        Assert.True(client.Capabilities.Contains(SmtpCapability.EightBitMime));
        Assert.True(client.Capabilities.Contains(SmtpCapability.Pipelining));
        Assert.Equal(1024 * 1024, (int)client.MaxSize);
        Assert.Contains("PLAIN", client.AuthenticationMechanisms);
        Assert.Contains("LOGIN", client.AuthenticationMechanisms);
        // TLS is off by default: advertising STARTTLS with a self-signed cert would break opportunistic clients.
        Assert.False(client.Capabilities.Contains(SmtpCapability.StartTLS));

        await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Message_is_trapped_and_listed()
    {
        var subject = "trap-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject, attachments: [TestMail.Csv()]));

        var item = await app.FindAsync(subject);

        Assert.Equal("\"Sender\" <sender@example.com>", item.GetProperty("from").GetString());
        Assert.Equal(1, item.GetProperty("attachmentCount").GetInt32());
        Assert.False(item.GetProperty("isRead").GetBoolean());
    }

    [Theory]
    [InlineData("user", "password")]
    [InlineData("anyone@anywhere", "")]
    public async Task Any_credentials_are_accepted(string user, string password)
    {
        using var client = await app.ConnectSmtpAsync(user, password);
        Assert.True(client.IsAuthenticated);
        await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Envelope_only_recipients_show_as_bcc_and_unicode_survives()
    {
        var subject = "Grüße 👋 " + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject, to: "visible@example.com", text: "Café ☕"),
            envelopeTo: ["visible@example.com", "secret@example.com"]);

        var id = (await app.FindAsync(subject)).GetProperty("id").GetString();
        var detail = await app.GetJsonAsync($"/api/messages/{id}");

        Assert.Equal(subject, detail.GetProperty("subject").GetString());
        Assert.Equal("secret@example.com", detail.GetProperty("bcc").GetString());
        Assert.Contains("Café ☕", detail.GetProperty("text").GetString());
        Assert.Equal("127.0.0.1", detail.GetProperty("clientIp").GetString());
    }

    [Fact]
    public async Task Oversize_message_is_rejected_with_552()
    {
        var big = TestMail.Build(subject: "too big", attachments: [("big.bin", "application/octet-stream", new byte[2 * 1024 * 1024])]);
        using var client = await app.ConnectSmtpAsync();

        var ex = await Assert.ThrowsAsync<SmtpCommandException>(() => client.SendAsync(FormatOptions.Default, new SendRequest(big), TestContext.Current.CancellationToken));

        Assert.Equal(552, (int)ex.StatusCode);
    }

    [Fact]
    public async Task Several_messages_on_one_connection()
    {
        var tag = Guid.NewGuid().ToString("N");
        using (var client = await app.ConnectSmtpAsync())
        {
            for (var i = 0; i < 3; i++) await client.SendAsync(FormatOptions.Default, new SendRequest(TestMail.Build(subject: $"{tag}-{i}")), TestContext.Current.CancellationToken);
            await client.DisconnectAsync(true, TestContext.Current.CancellationToken);
        }

        for (var i = 0; i < 3; i++) await app.FindAsync($"{tag}-{i}");
    }
}
