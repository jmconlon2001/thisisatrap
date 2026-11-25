using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ThisIsATrap.Analysis;

public sealed class SpamAssassinOptions
{
    /// <summary>spamd host. Empty (the default) turns SpamAssassin scoring off.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; } = 783;
    public int TimeoutSeconds { get; set; } = 15;
    public bool Enabled => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>Minimal spamd client: sends a message with the SPAMC REPORT command and parses the score and rule table.</summary>
public sealed partial class SpamAssassinClient(SpamAssassinOptions options)
{
    public bool Enabled => options.Enabled;

    public async Task<SpamAssassinResult> CheckAsync(byte[] message, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(options.Host, options.Port, timeout.Token);
            await using var stream = tcp.GetStream();

            var header = Encoding.ASCII.GetBytes($"REPORT SPAMC/1.5\r\nContent-length: {message.Length}\r\nUser: thisisatrap\r\n\r\n");
            await stream.WriteAsync(header, timeout.Token);
            await stream.WriteAsync(message, timeout.Token);
            tcp.Client.Shutdown(SocketShutdown.Send);

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var response = await reader.ReadToEndAsync(timeout.Token);
            return Parse(response);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return new SpamAssassinResult(false, 0, 0, false, [], $"SpamAssassin unavailable at {options.Host}:{options.Port}: {ex.Message}");
        }
    }

    /// <summary>Parses a spamd REPORT response ("SPAMD/1.1 0 EX_OK", "Spam: True ; 7.2 / 5.0", rule table).</summary>
    public static SpamAssassinResult Parse(string response)
    {
        var status = StatusRx().Match(response);
        if (!status.Success || status.Groups[1].Value != "0")
            return new SpamAssassinResult(false, 0, 0, false, [], "Unexpected spamd response: " + response.Split('\n')[0].Trim());

        var spam = SpamHeaderRx().Match(response);
        if (!spam.Success) return new SpamAssassinResult(false, 0, 0, false, [], "spamd response had no Spam: header");

        var rules = new List<SpamRuleHit>();
        foreach (Match m in RuleLineRx().Matches(response))
        {
            var score = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            rules.Add(new SpamRuleHit(m.Groups[2].Value, score, m.Groups[3].Value.Trim()));
        }
        return new SpamAssassinResult(
            true,
            double.Parse(spam.Groups[2].Value, CultureInfo.InvariantCulture),
            double.Parse(spam.Groups[3].Value, CultureInfo.InvariantCulture),
            spam.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase) || spam.Groups[1].Value.Equals("yes", StringComparison.OrdinalIgnoreCase),
            rules.OrderByDescending(r => r.Score).ToList(),
            null);
    }

    [GeneratedRegex(@"^SPAMD/\d+\.\d+\s+(\d+)", RegexOptions.Multiline)] private static partial Regex StatusRx();
    [GeneratedRegex(@"^Spam:\s*(\w+)\s*;\s*(-?[\d.]+)\s*/\s*(-?[\d.]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)] private static partial Regex SpamHeaderRx();
    [GeneratedRegex(@"^\s*(-?\d+(?:\.\d+)?)\s+([A-Z][A-Z0-9_]+)\s+(.+)$", RegexOptions.Multiline)] private static partial Regex RuleLineRx();
}
