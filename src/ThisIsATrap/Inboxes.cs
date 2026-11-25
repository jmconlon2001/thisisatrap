using System.Text;

namespace ThisIsATrap;

/// <summary>A separate mailbox. Mail is routed to it by SMTP username (or the X-Trap-Inbox header).</summary>
public sealed record Inbox(long Id, string Slug, string Name, DateTimeOffset CreatedUtc, PurgeSettings? Purge)
{
    public const long DefaultId = 1;
    public const string DefaultSlug = "default";
    public const string DefaultName = "Default";

    /// <summary>Header that routes a message when the client can't authenticate.</summary>
    public const string RoutingHeader = "X-Trap-Inbox";

    public bool IsDefault => Id == DefaultId;
}

/// <summary>An inbox with its message counts, for the sidebar and the API.</summary>
public sealed record InboxSummary(
    long Id, string Slug, string Name, DateTimeOffset CreatedUtc, bool IsDefault,
    PurgeSettings? Purge, int Total, int Unread, long TotalBytes);

public static class InboxSlug
{
    public const int MaxLength = 64;

    /// <summary>
    /// Turns an SMTP username, header value or display name into a URL-safe inbox key:
    /// lowercase a-z 0-9 . _ - only, runs of anything else become one '-', trimmed, at most 64 chars.
    /// Returns null when nothing usable is left.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder(Math.Min(value.Length, MaxLength));
        var pendingDash = false;
        foreach (var raw in value.Trim().Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(raw) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(raw);
            var ok = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
            if (!ok || c == '-') { pendingDash = sb.Length > 0; continue; }
            if (pendingDash) { sb.Append('-'); pendingDash = false; }
            sb.Append(c);
            if (sb.Length >= MaxLength) break;
        }
        var slug = sb.ToString().Trim('.', '_', '-');
        if (slug.Length > MaxLength) slug = slug[..MaxLength].Trim('.', '_', '-');
        return slug.Length == 0 ? null : slug;
    }
}
