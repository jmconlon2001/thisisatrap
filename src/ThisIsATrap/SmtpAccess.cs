using System.Net;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using ThisIsATrap.Health;

namespace ThisIsATrap;

/// <summary>Optional allowlist of client addresses that may send mail. Off by default (everyone may send).</summary>
public sealed record SmtpAccessSettings
{
    public bool Enabled { get; init; }
    /// <summary>Single addresses ("192.168.1.20", "::1") or CIDR ranges ("10.0.0.0/8", "fd00::/8").</summary>
    public List<string> Allow { get; init; } = [];
    /// <summary>Always let this machine send (127.0.0.1 / ::1), so local tools and health checks keep working.</summary>
    public bool AlwaysAllowLoopback { get; init; } = true;
}

/// <summary>Stores, validates and evaluates the SMTP client allowlist.</summary>
public sealed class SmtpAccess(MailStore store)
{
    public const string SettingKey = "smtp-access";
    public const int MaxEntries = 200;

    private volatile Compiled? _compiled;

    private sealed record Compiled(SmtpAccessSettings Settings, IPNetwork[] Networks);

    public SmtpAccessSettings Get() => store.GetSetting<SmtpAccessSettings>(SettingKey) ?? new SmtpAccessSettings();

    /// <summary>Validates and saves. Entries are normalised (e.g. "10.1.2.3" → "10.1.2.3/32"). Throws on an invalid entry.</summary>
    public SmtpAccessSettings Save(SmtpAccessSettings settings)
    {
        var entries = new List<string>();
        foreach (var raw in settings.Allow ?? [])
        {
            var text = raw?.Trim() ?? "";
            if (text.Length == 0 || text.StartsWith('#')) continue;
            if (!TryParseEntry(text, out var network)) throw new ArgumentException($"Not an IP address or CIDR range: '{text}'");
            var normalized = network.ToString();
            if (!entries.Contains(normalized)) entries.Add(normalized);
        }
        if (entries.Count > MaxEntries) throw new ArgumentException($"At most {MaxEntries} entries.");
        if (settings.Enabled && entries.Count == 0 && !settings.AlwaysAllowLoopback)
            throw new ArgumentException("An empty allowlist without localhost would block every client. Add an address or allow localhost.");

        var clean = settings with { Allow = entries };
        store.SaveSetting(SettingKey, clean);
        _compiled = null;
        return clean;
    }

    /// <summary>True when the allowlist is off, or <paramref name="ip"/> is on it (or is loopback and loopback is allowed).</summary>
    public bool IsAllowed(IPAddress? ip)
    {
        var c = _compiled ??= Compile(Get());
        if (!c.Settings.Enabled) return true;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (c.Settings.AlwaysAllowLoopback && IPAddress.IsLoopback(ip)) return true;
        return c.Networks.Any(n => n.Contains(ip));
    }

    /// <summary>"10.0.0.5" (a single host) or "10.0.0.0/8". IPv4-mapped IPv6 forms are unwrapped.</summary>
    public static bool TryParseEntry(string text, out IPNetwork network)
    {
        network = default;
        text = text.Trim();
        if (text.Contains('/'))
        {
            // Reject "10.0.0.1/24": host bits set means it's probably a typo for /32 or 10.0.0.0/24.
            if (!IPNetwork.TryParse(text, out network) || !IPAddress.TryParse(text[..text.IndexOf('/')], out var given)) return false;
            if (!given.Equals(network.BaseAddress)) return false;
            return Canonical(ref network);
        }
        if (!IPAddress.TryParse(text, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        network = new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }

    private static bool Canonical(ref IPNetwork network)
    {
        if (network.BaseAddress.IsIPv4MappedToIPv6 && network.PrefixLength >= 96)
            network = new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
        return true;
    }

    private static Compiled Compile(SmtpAccessSettings s) =>
        new(s, s.Allow.Select(e => TryParseEntry(e, out var n) ? (IPNetwork?)n : null).OfType<IPNetwork>().ToArray());

    public static IPAddress? RemoteAddress(ISessionContext context) =>
        context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep) && ep is IPEndPoint ip ? ip.Address : null;
}

/// <summary>Rejects MAIL FROM (and RCPT TO, defensively) from clients that aren't on the allowlist.</summary>
public sealed class SmtpAccessFilter(SmtpAccess access, EventHub events, RuntimeStats stats, ILogger log) : IMailboxFilter
{
    public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken ct) => Check(context, "from");

    public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken ct) => Check(context, "rcpt");

    private Task<bool> Check(ISessionContext context, string stage)
    {
        var ip = SmtpAccess.RemoteAddress(context);
        if (access.IsAllowed(ip)) return Task.FromResult(true);

        if (stage == "from")
        {
            stats.MessageRejected();
            log.LogWarning("SMTP client {Ip} is not on the allowlist; rejected", ip);
            events.Publish("rejected", new { stage = "access", address = ip?.ToString() ?? "unknown", code = 554, enhancedCode = "5.7.1", message = "Client host is not on the allowlist" });
        }
        throw new SmtpResponseException(new SmtpResponse(SmtpReplyCode.TransactionFailed, $"5.7.1 Client host {ip} is not allowed to send mail here"));
    }
}
