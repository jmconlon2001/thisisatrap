using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using ThisIsATrap.Tests;

namespace ThisIsATrap.IntegrationTests;

/// <summary>An instance with STARTTLS on the main port and implicit TLS on a second port.</summary>
public sealed class TlsApp : TrapApp
{
    public int SmtpsPort { get; } = Free();

    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["ThisIsATrap:Tls:StartTls"] = "true",
        ["ThisIsATrap:Tls:SmtpsPort"] = SmtpsPort.ToString(),
    };

    private static int Free()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

public sealed class TlsTests(TlsApp app) : IClassFixture<TlsApp>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Connects and returns the thumbprint of the certificate the server presented.</summary>
    private async Task<(SmtpClient Client, string? Seen)> Connect(int port, SecureSocketOptions mode)
    {
        string? seen = null;
        var client = new SmtpClient
        {
            // The self-signed certificate isn't trusted by the OS; record it and accept for the test.
            ServerCertificateValidationCallback = (_, cert, _, _) => { seen = cert?.GetCertHashString(); return true; },
        };
        await client.ConnectAsync("127.0.0.1", port, mode, Ct);
        return (client, seen);
    }

    [Fact]
    public async Task Starttls_is_advertised_negotiated_and_mail_is_trapped()
    {
        var (client, seen) = await Connect(app.SmtpPort, SecureSocketOptions.StartTls);
        using (client)
        {
            Assert.True(client.IsSecure);
            Assert.NotNull(seen);
            await client.SendAsync(FormatOptions.Default, new MailKit.SendRequest(TestMail.Build(subject: "over-starttls")), Ct);
            await client.DisconnectAsync(true, Ct);
        }
        await app.FindAsync("over-starttls");
    }

    [Fact]
    public async Task Plain_clients_still_work_when_starttls_is_offered()
    {
        using var client = await app.ConnectSmtpAsync();
        Assert.True(client.Capabilities.Contains(SmtpCapability.StartTLS));
        Assert.False(client.IsSecure);
        await client.SendAsync(FormatOptions.Default, new MailKit.SendRequest(TestMail.Build(subject: "plain-with-starttls-offered")), Ct);
        await app.FindAsync("plain-with-starttls-offered");
    }

    [Fact]
    public async Task Implicit_tls_port_accepts_mail()
    {
        var (client, seen) = await Connect(app.SmtpsPort, SecureSocketOptions.SslOnConnect);
        using (client)
        {
            Assert.True(client.IsSecure);
            Assert.NotNull(seen);
            await client.AuthenticateAsync("tls-user", "x", Ct);
            await client.SendAsync(FormatOptions.Default, new MailKit.SendRequest(TestMail.Build(subject: "over-smtps")), Ct);
            await client.DisconnectAsync(true, Ct);
        }
        Assert.Equal("tls-user", (await app.FindAsync("over-smtps")).GetProperty("inbox").GetString());
    }

    [Fact]
    public async Task Certificate_download_matches_what_the_server_presents()
    {
        var (client, seen) = await Connect(app.SmtpsPort, SecureSocketOptions.SslOnConnect);
        await client.DisconnectAsync(true, Ct);
        client.Dispose();
        using var http = app.CreateClient();

        var der = await http.GetByteArrayAsync("/api/tls/certificate", Ct);
        var pem = await http.GetStringAsync("/api/tls/certificate?format=pem", Ct);
        var downloaded = X509CertificateLoader.LoadCertificate(der);

        Assert.Equal(seen, downloaded.Thumbprint);
        Assert.False(downloaded.HasPrivateKey);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem);
        Assert.DoesNotContain("PRIVATE KEY", pem);
    }

    [Fact]
    public async Task Settings_and_status_describe_tls()
    {
        var tls = (await app.GetJsonAsync("/api/settings")).GetProperty("tls");
        Assert.True(tls.GetProperty("enabled").GetBoolean());
        Assert.True(tls.GetProperty("startTls").GetBoolean());
        Assert.Equal(app.SmtpsPort, tls.GetProperty("smtpsPort").GetInt32());
        Assert.True(tls.GetProperty("certificate").GetProperty("selfSigned").GetBoolean());

        var status = await app.GetJsonAsync("/api/status");
        Assert.Contains("(STARTTLS)", status.GetProperty("smtp").GetProperty("status").GetString());
        Assert.Equal("Healthy", status.GetProperty("health").GetProperty("status").GetString());
    }
}

[Collection(AppCollection.Name)]
public sealed class NoTlsTests(TrapApp app)
{
    [Fact]
    public async Task Certificate_endpoint_is_404_and_settings_say_off()
    {
        using var http = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/tls/certificate", TestContext.Current.CancellationToken)).StatusCode);
        Assert.False((await app.GetJsonAsync("/api/settings")).GetProperty("tls").GetProperty("enabled").GetBoolean());
    }
}
