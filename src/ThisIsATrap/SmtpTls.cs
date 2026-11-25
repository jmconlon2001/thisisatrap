using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace ThisIsATrap;

public sealed record CertificateInfo(string Subject, string Thumbprint, DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool SelfSigned, IReadOnlyList<string> DnsNames, string Source);

/// <summary>
/// The SMTP listener's TLS certificate: a configured PFX, or a self-signed one generated once and
/// kept in the data folder (smtp-cert.pfx) so clients that pin or trust it keep working across restarts.
/// </summary>
public sealed class SmtpTls
{
    public const string GeneratedFileName = "smtp-cert.pfx";

    private readonly Lazy<X509Certificate2?> _certificate;

    public SmtpTls(IOptions<TrapOptions> options, MailStore store, ILogger<SmtpTls> log, TimeProvider? time = null)
    {
        var o = options.Value;
        Options = o.Tls;
        _certificate = new(() => o.Tls.Enabled ? LoadOrCreate(o.Tls, store.DataPath, o.ServerName, time ?? TimeProvider.System, log) : null);
    }

    public SmtpTlsOptions Options { get; }
    public bool Enabled => Options.Enabled;

    /// <summary>The certificate, or null when TLS is off. Created on first use.</summary>
    public X509Certificate2? Certificate => _certificate.Value;

    public CertificateInfo? Info => Certificate is { } c ? Describe(c, string.IsNullOrWhiteSpace(Options.CertificatePath) ? "generated" : "configured") : null;

    public static X509Certificate2 LoadOrCreate(SmtpTlsOptions tls, string dataPath, string serverName, TimeProvider time, ILogger log)
    {
        if (!string.IsNullOrWhiteSpace(tls.CertificatePath))
        {
            var cert = Load(File.ReadAllBytes(tls.CertificatePath), tls.CertificatePassword);
            if (!cert.HasPrivateKey) throw new InvalidOperationException($"The certificate in {tls.CertificatePath} has no private key.");
            log.LogInformation("SMTP TLS certificate loaded from {Path} ({Subject}, expires {NotAfter:yyyy-MM-dd})", tls.CertificatePath, cert.Subject, cert.NotAfter);
            return cert;
        }

        var path = Path.Combine(dataPath, GeneratedFileName);
        if (File.Exists(path))
        {
            try
            {
                var existing = Load(File.ReadAllBytes(path), null);
                if (existing.HasPrivateKey && existing.NotAfter.ToUniversalTime() > time.GetUtcNow().UtcDateTime.AddDays(30)) return existing;
                log.LogInformation("Self-signed SMTP certificate expires soon; generating a new one");
            }
            catch (CryptographicException ex)
            {
                log.LogWarning(ex, "Could not read {Path}; generating a new self-signed certificate", path);
            }
        }

        var pfx = CreateSelfSigned(serverName, time.GetUtcNow());
        File.WriteAllBytes(path, pfx);
        RestrictToOwner(path);
        var created = Load(pfx, null);
        log.LogInformation("Generated self-signed SMTP certificate {Thumbprint} for {Names} in {Path}", created.Thumbprint, serverName, path);
        return created;
    }

    /// <summary>A 5-year RSA-2048 server certificate for the server name, this machine and localhost. Returns PFX bytes.</summary>
    public static byte[] CreateSelfSigned(string serverName, DateTimeOffset now)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={serverName}, O=ThisIsATrap (self-signed)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in new[] { serverName, Environment.MachineName, "localhost" }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            san.AddDnsName(name);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
        return cert.Export(X509ContentType.Pfx);
    }

    public static CertificateInfo Describe(X509Certificate2 c, string source)
    {
        var names = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).ToList();
        return new CertificateInfo(c.Subject, c.Thumbprint, c.NotBefore.ToUniversalTime(), c.NotAfter.ToUniversalTime(),
            c.Subject == c.Issuer, names, source);
    }

    /// <summary>
    /// Windows' TLS stack (Schannel) can't use ephemeral keys for a server certificate; elsewhere
    /// ephemeral keys avoid writing key files to the user profile.
    /// </summary>
    private static X509Certificate2 Load(byte[] pfx, string? password) =>
        X509CertificateLoader.LoadPkcs12(pfx, password,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // On Windows the data folder's ACL (app pool identity only, set by the deploy script) applies.
    }
}
