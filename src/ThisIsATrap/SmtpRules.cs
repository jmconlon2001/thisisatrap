using System.Text.RegularExpressions;
using SmtpServer;
using SmtpServer.Mail;
using SmtpServer.Protocol;
using SmtpServer.Storage;
using ThisIsATrap.Health;

namespace ThisIsATrap;

/// <summary>
/// One error-simulation rule: when the sender (MAIL FROM) or a recipient (RCPT TO) matches
/// <see cref="Pattern"/>, wait <see cref="DelayMs"/> and answer with <see cref="Code"/>.
/// A 2xx code just delays and then accepts (a "slow server").
/// </summary>
public sealed record SmtpRule
{
    public bool Enabled { get; init; } = true;
    /// <summary>"rcpt" (RCPT TO) or "from" (MAIL FROM).</summary>
    public string Match { get; init; } = "rcpt";
    /// <summary>Glob on the address: * = any characters, ? = one character. Case-insensitive.</summary>
    public string Pattern { get; init; } = "";
    public int Code { get; init; } = 550;
    public string EnhancedCode { get; init; } = "5.1.1";
    public string Message { get; init; } = "Mailbox unavailable";
    public int DelayMs { get; init; }
}

public sealed record RuleMatch(SmtpRule Rule, string Address, string Stage);

/// <summary>The error-simulation rule set: stored in the settings table, compiled once, matched per address.</summary>
public sealed class SmtpRules(MailStore store, ILogger<SmtpRules> log)
{
    public const string SettingKey = "smtp-rules";
    public const int MaxRules = 50;
    public const int MaxDelayMs = 120_000;

    /// <summary>Ready-made rules on reserved .test domains, so they can never collide with real mail.</summary>
    public static readonly IReadOnlyList<SmtpRule> Defaults =
    [
        new() { Pattern = "*@reject.test", Code = 550, EnhancedCode = "5.1.1", Message = "User unknown (simulated by ThisIsATrap)" },
        new() { Pattern = "*@full.test", Code = 452, EnhancedCode = "4.2.2", Message = "Mailbox full (simulated by ThisIsATrap)" },
        new() { Pattern = "*@tempfail.test", Code = 451, EnhancedCode = "4.3.0", Message = "Temporary failure, try again later (simulated by ThisIsATrap)" },
        new() { Pattern = "*@slow.test", Code = 250, EnhancedCode = "2.1.5", Message = "OK (after a simulated 10 second delay)", DelayMs = 10_000 },
        new() { Enabled = false, Match = "from", Pattern = "*@blocked-sender.test", Code = 553, EnhancedCode = "5.7.1", Message = "Sender rejected (simulated by ThisIsATrap)" },
    ];

    private volatile Compiled[]? _compiled;

    private sealed record Compiled(SmtpRule Rule, Regex Regex);

    public IReadOnlyList<SmtpRule> Get() => store.GetSetting<List<SmtpRule>>(SettingKey) ?? [.. Defaults];

    public IReadOnlyList<SmtpRule> Save(IEnumerable<SmtpRule> rules)
    {
        var clean = rules.Take(MaxRules).Select(Validate).ToList();
        store.SaveSetting(SettingKey, clean);
        _compiled = null;
        return clean;
    }

    public RuleMatch? Find(string stage, string address)
    {
        var compiled = _compiled ??= Get().Where(r => r.Enabled && r.Pattern.Length > 0).Select(r => new Compiled(r, ToRegex(r.Pattern))).ToArray();
        foreach (var c in compiled)
        {
            if (!string.Equals(c.Rule.Match, stage, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (c.Regex.IsMatch(address)) return new RuleMatch(c.Rule, address, stage); }
            catch (RegexMatchTimeoutException ex) { log.LogWarning(ex, "Rule pattern {Pattern} timed out", c.Rule.Pattern); }
        }
        return null;
    }

    /// <summary>Glob → anchored, case-insensitive regex. NonBacktracking + a timeout so no pattern can stall the SMTP session.</summary>
    public static Regex ToRegex(string glob) => new(
        "^" + Regex.Escape(glob.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public static SmtpRule Validate(SmtpRule r)
    {
        var code = Math.Clamp(r.Code, 200, 599);
        if (code is >= 300 and < 400) code = 450;   // 3xx makes no sense for MAIL/RCPT
        var match = string.Equals(r.Match, "from", StringComparison.OrdinalIgnoreCase) ? "from" : "rcpt";
        var pattern = (r.Pattern ?? "").Trim();
        if (pattern.Length > 254) pattern = pattern[..254];
        var message = Regex.Replace(r.Message ?? "", @"[\r\n]+", " ").Trim();   // no header/line injection into the SMTP reply
        if (message.Length > 200) message = message[..200];
        var enhanced = Regex.IsMatch(r.EnhancedCode ?? "", @"^[245]\.\d{1,3}\.\d{1,3}$") ? r.EnhancedCode! : $"{code / 100}.0.0";
        return r with { Match = match, Pattern = pattern, Code = code, EnhancedCode = enhanced, Message = message.Length == 0 ? "Simulated response" : message, DelayMs = Math.Clamp(r.DelayMs, 0, MaxDelayMs) };
    }
}

/// <summary>SmtpServer hook that applies the error-simulation rules to MAIL FROM and RCPT TO.</summary>
public sealed class SmtpRuleFilter(SmtpRules rules, EventHub events, RuntimeStats stats, TimeProvider time, ILogger log) : IMailboxFilter
{
    public Task<bool> CanAcceptFromAsync(ISessionContext context, IMailbox from, int size, CancellationToken ct) =>
        ApplyAsync("from", from, ct);

    public Task<bool> CanDeliverToAsync(ISessionContext context, IMailbox to, IMailbox from, CancellationToken ct) =>
        ApplyAsync("rcpt", to, ct);

    private async Task<bool> ApplyAsync(string stage, IMailbox mailbox, CancellationToken ct)
    {
        var address = mailbox is null || string.IsNullOrEmpty(mailbox.User) ? "" : $"{mailbox.User}@{mailbox.Host}";
        if (rules.Find(stage, address) is not { } match) return true;

        if (match.Rule.DelayMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(match.Rule.DelayMs), time, ct);
        if (match.Rule.Code < 300) return true;

        stats.MessageRejected();
        log.LogInformation("Simulated {Code} for {Stage} {Address}", match.Rule.Code, stage, address);
        events.Publish("rejected", new { stage, address, code = match.Rule.Code, enhancedCode = match.Rule.EnhancedCode, message = match.Rule.Message });
        throw new SmtpResponseException(new SmtpResponse((SmtpReplyCode)match.Rule.Code, $"{match.Rule.EnhancedCode} {match.Rule.Message}"));
    }
}
