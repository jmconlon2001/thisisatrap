using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ThisIsATrap.Tests;

public sealed class SmtpTlsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(DateTimeOffset.UtcNow);

    public void Dispose() => _dir.Dispose();

    private X509Certificate2 Load(SmtpTlsOptions tls, string serverName = "trap.test") =>
        SmtpTls.LoadOrCreate(tls, _dir.Path, serverName, _time, NullLogger.Instance);

    [Fact]
    public void Generates_a_server_certificate_with_names_and_key()
    {
        var cert = Load(new SmtpTlsOptions { StartTls = true });
        var info = SmtpTls.Describe(cert, "generated");

        Assert.True(cert.HasPrivateKey);
        Assert.Contains("CN=trap.test", cert.Subject);
        Assert.True(info.SelfSigned);
        Assert.Contains("trap.test", info.DnsNames);
        Assert.Contains("localhost", info.DnsNames);
        Assert.Contains(Environment.MachineName, info.DnsNames, StringComparer.OrdinalIgnoreCase);
        Assert.InRange(cert.NotAfter.ToUniversalTime(), _time.Now.UtcDateTime.AddYears(5).AddDays(-2), _time.Now.UtcDateTime.AddYears(5).AddDays(1));
        var eku = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
        Assert.Contains(eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
        Assert.True(File.Exists(Path.Combine(_dir.Path, SmtpTls.GeneratedFileName)));
    }

    [Fact]
    public void Generated_certificate_is_reused_across_restarts()
    {
        var first = Load(new SmtpTlsOptions { StartTls = true });
        var second = Load(new SmtpTlsOptions { StartTls = true });
        Assert.Equal(first.Thumbprint, second.Thumbprint);
    }

    [Fact]
    public void Certificate_close_to_expiry_is_replaced()
    {
        var first = Load(new SmtpTlsOptions { StartTls = true });
        _time.Now = _time.Now.AddYears(5).AddDays(-10);   // inside the 30-day renewal window
        var renewed = Load(new SmtpTlsOptions { StartTls = true });
        Assert.NotEqual(first.Thumbprint, renewed.Thumbprint);
    }

    [Fact]
    public void Corrupt_generated_file_is_replaced()
    {
        File.WriteAllText(Path.Combine(_dir.Path, SmtpTls.GeneratedFileName), "not a pfx");
        Assert.True(Load(new SmtpTlsOptions { StartTls = true }).HasPrivateKey);
    }

    [Fact]
    public void Configured_pfx_with_password_is_used()
    {
        var pfxPath = Path.Combine(_dir.Path, "mine.pfx");
        using (var generated = X509CertificateLoader.LoadPkcs12(SmtpTls.CreateSelfSigned("mail.company.test", DateTimeOffset.UtcNow), null, X509KeyStorageFlags.Exportable))
            File.WriteAllBytes(pfxPath, generated.Export(X509ContentType.Pfx, "s3cret"));

        var cert = Load(new SmtpTlsOptions { SmtpsPort = 465, CertificatePath = pfxPath, CertificatePassword = "s3cret" });

        Assert.Contains("CN=mail.company.test", cert.Subject);
        Assert.False(File.Exists(Path.Combine(_dir.Path, SmtpTls.GeneratedFileName)));
    }

    [Fact]
    public void Configured_pfx_errors_surface()
    {
        Assert.ThrowsAny<IOException>(() => Load(new SmtpTlsOptions { StartTls = true, CertificatePath = Path.Combine(_dir.Path, "missing.pfx") }));

        var pfxPath = Path.Combine(_dir.Path, "pw.pfx");
        using (var generated = X509CertificateLoader.LoadPkcs12(SmtpTls.CreateSelfSigned("x", DateTimeOffset.UtcNow), null, X509KeyStorageFlags.Exportable))
            File.WriteAllBytes(pfxPath, generated.Export(X509ContentType.Pfx, "right"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            Load(new SmtpTlsOptions { StartTls = true, CertificatePath = pfxPath, CertificatePassword = "wrong" }));
    }

    [Fact]
    public void Public_certificate_only_file_is_rejected()
    {
        var cerPath = Path.Combine(_dir.Path, "public.p12");
        using (var generated = X509CertificateLoader.LoadPkcs12(SmtpTls.CreateSelfSigned("x", DateTimeOffset.UtcNow), null))
        using (var publicOnly = X509CertificateLoader.LoadCertificate(generated.Export(X509ContentType.Cert)))
            File.WriteAllBytes(cerPath, publicOnly.Export(X509ContentType.Pkcs12));   // a PKCS#12 with no private key

        var ex = Assert.ThrowsAny<Exception>(() => Load(new SmtpTlsOptions { StartTls = true, CertificatePath = cerPath }));
        Assert.Contains("private key", ex.Message);
    }

    [Fact]
    public void Disabled_tls_creates_nothing()
    {
        var options = Options.Create(new TrapOptions { DataPath = _dir.Path });
        var tls = new SmtpTls(options, TestStore.Create(_dir), NullLogger<SmtpTls>.Instance);

        Assert.False(tls.Enabled);
        Assert.Null(tls.Certificate);
        Assert.Null(tls.Info);
        Assert.False(File.Exists(Path.Combine(_dir.Path, SmtpTls.GeneratedFileName)));
    }

    [Theory]
    [InlineData(false, null, "listening on port 25")]
    [InlineData(true, null, "listening on port 25 (STARTTLS)")]
    [InlineData(false, 465, "listening on port 25, 465 (TLS)")]
    [InlineData(true, 465, "listening on port 25 (STARTTLS), 465 (TLS)")]
    public void Status_describes_endpoints(bool startTls, int? smtps, string expected) =>
        Assert.Equal(expected, SmtpListenerService.ListeningStatus(new TrapOptions { SmtpPort = 25, Tls = new SmtpTlsOptions { StartTls = startTls, SmtpsPort = smtps } }));
}
