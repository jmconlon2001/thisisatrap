using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using MimeKit;

namespace ThisIsATrap.Analysis;

/// <summary>One rule that matched. Score uses SpamAssassin's scale (positive = more spammy).</summary>
public sealed record SpamRuleHit(string Id, double Score, string Description, string? Detail = null);

public sealed record SpamAssassinResult(bool Available, double Score, double Threshold, bool IsSpam, IReadOnlyList<SpamRuleHit> Rules, string? Error);

public sealed record SpamReport(
    double Score, double Threshold, string Verdict, string Source,
    IReadOnlyList<SpamRuleHit> Rules, SpamAssassinResult? SpamAssassin);

/// <summary>
/// Built-in content checks modelled on common SpamAssassin rules, plus problems specific to
/// test mail (dev URLs leaking into links, Gmail clipping, missing one-click unsubscribe…).
/// </summary>
public static partial class SpamCheck
{
    public const double Threshold = 5.0;

    private static readonly string[] Shorteners = ["bit.ly", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "buff.ly", "rebrand.ly", "cutt.ly", "shorturl.at", "rb.gy", "tiny.cc"];

    private static readonly string[] SpamPhrases =
    [
        "act now", "100% free", "risk-free", "risk free", "winner", "congratulations", "you have been selected", "no credit check",
        "earn money", "make money fast", "extra cash", "cash bonus", "free gift", "lowest price", "limited time offer", "urgent",
        "guaranteed", "double your", "click here now", "once in a lifetime", "no obligation", "100% satisfied", "miracle", "viagra",
        "crypto giveaway", "wire transfer", "verify your account", "your account has been suspended",
    ];

    private static readonly string[] RiskyExtensions = [".exe", ".js", ".scr", ".vbs", ".bat", ".cmd", ".jar", ".msi", ".docm", ".xlsm", ".pptm", ".iso", ".lnk", ".hta", ".ps1", ".com", ".pif"];

    public static IReadOnlyList<SpamRuleHit> Run(MimeMessage m, string envelopeFrom, long size)
    {
        var hits = new List<SpamRuleHit>();
        var html = m.HtmlBody;
        var text = m.TextBody;

        // ---- structure / headers
        if (html is not null && string.IsNullOrWhiteSpace(text))
            hits.Add(new("MIME_HTML_ONLY", 0.7, "Message has an HTML part but no plain-text alternative"));
        if (html is null && string.IsNullOrWhiteSpace(text))
            hits.Add(new("EMPTY_MESSAGE", 1.5, "Message has no readable body"));
        if (!m.Headers.Contains(HeaderId.Date))
            hits.Add(new("MISSING_DATE", 1.4, "Missing Date: header"));
        if (!m.Headers.Contains(HeaderId.MessageId))
            hits.Add(new("MISSING_MID", 0.5, "Missing Message-Id: header"));
        if (!m.Headers.Contains(HeaderId.MimeVersion) && m.Body is Multipart)
            hits.Add(new("MISSING_MIMEOLE", 0.5, "Multipart message without a MIME-Version: header"));
        if (m.From.Mailboxes.FirstOrDefault() is not { } from)
            hits.Add(new("NO_FROM", 2.0, "Missing or invalid From: header"));
        else
        {
            var fromDomain = Domain(from.Address);
            var envDomain = Domain(envelopeFrom);
            if (envDomain is not null && fromDomain is not null && !SameOrgDomain(fromDomain, envDomain))
                hits.Add(new("FROM_ENVELOPE_MISMATCH", 0.5, "From: domain differs from the envelope sender, so SPF/DMARC alignment would fail", $"{fromDomain} vs {envDomain}"));
            if (m.ReplyTo.Mailboxes.FirstOrDefault() is { } reply && Domain(reply.Address) is { } replyDomain && fromDomain is not null && !SameOrgDomain(replyDomain, fromDomain))
                hits.Add(new("REPLYTO_DIFF_DOMAIN", 0.3, "Reply-To: points at a different domain than From:", replyDomain));
        }

        // ---- subject
        var subject = m.Subject ?? "";
        if (subject.Trim().Length == 0)
            hits.Add(new("MISSING_SUBJECT", 1.8, "Missing or empty Subject: header"));
        else
        {
            var letters = subject.Where(char.IsLetter).ToList();
            if (letters.Count >= 8 && letters.Count(char.IsUpper) >= letters.Count * 0.8)
                hits.Add(new("SUBJ_ALL_CAPS", 1.5, "Subject is mostly capital letters", subject));
            if (ExcessPunctRx().IsMatch(subject))
                hits.Add(new("SUBJ_EXCESS_PUNCT", 1.0, "Subject has repeated !, ? or $", subject));
        }

        // ---- content
        var visibleText = html is not null ? VisibleText(html) : text ?? "";
        var lower = (subject + " " + visibleText).ToLowerInvariant();
        var phrases = SpamPhrases.Where(p => lower.Contains(p, StringComparison.Ordinal)).ToList();
        if (phrases.Count > 0)
            hits.Add(new("SPAMMY_PHRASES", Math.Min(2.5, 0.5 * phrases.Count), $"Uses {phrases.Count} phrase(s) common in spam", string.Join(", ", phrases.Take(6))));

        if (html is not null)
        {
            var doc = new HtmlParser().ParseDocument(html);
            var images = doc.QuerySelectorAll("img").ToList();
            if (images.Count > 0 && visibleText.Length < 400)
                hits.Add(new("HTML_IMAGE_RATIO", 1.2, "Mostly images with little text; filters treat image-only mail as suspicious", $"{images.Count} image(s), {visibleText.Length} characters of text"));
            var noAlt = images.Count(i => !i.HasAttribute("alt"));
            if (noAlt > 0)
                hits.Add(new("HTML_IMG_NO_ALT", 0.3, "Images without alt text (hurts accessibility and blocked-image rendering)", $"{noAlt} of {images.Count}"));
            var hidden = doc.QuerySelectorAll("[style]").Where(e => HiddenStyleRx().IsMatch(e.GetAttribute("style")!) && e.TextContent.Trim().Length > 20).ToList();
            if (hidden.Count > 0)
                hits.Add(new("HTML_HIDDEN_TEXT", 0.8, "Contains hidden text (display:none, font-size:0 …); keep preheaders short", Trim(hidden[0].TextContent)));
            var htmlBytes = System.Text.Encoding.UTF8.GetByteCount(html);
            if (htmlBytes > 102 * 1024)
                hits.Add(new("HTML_CLIPPED_GMAIL", 1.0, "HTML is over 102 KB; Gmail clips it behind a \"View entire message\" link", $"{htmlBytes / 1024} KB"));
            if (doc.QuerySelectorAll("a[href]").Any(a => UnsubscribeRx().IsMatch(a.TextContent + " " + a.GetAttribute("href"))) && !m.Headers.Contains("List-Unsubscribe"))
                hits.Add(new("NO_LIST_UNSUBSCRIBE", 1.0, "Has an unsubscribe link but no List-Unsubscribe header (required by Gmail/Yahoo for bulk mail)"));
        }
        if (m.Headers.Contains("List-Unsubscribe") && !m.Headers.Contains("List-Unsubscribe-Post"))
            hits.Add(new("NO_ONE_CLICK_UNSUB", 0.3, "List-Unsubscribe without List-Unsubscribe-Post: One-Click (RFC 8058)"));
        if (size > 10L * 1024 * 1024)
            hits.Add(new("LARGE_MESSAGE", 0.5, "Message is larger than 10 MB; many servers reject it", $"{size / 1024 / 1024} MB"));

        // ---- links
        var links = LinkExtractor.Extract(html, text);
        var shortened = links.Select(l => LinkExtractor.HttpHost(l.Url)).OfType<string>().Where(h => Shorteners.Contains(h)).Distinct().ToList();
        if (shortened.Count > 0)
            hits.Add(new("URL_SHORTENER", 1.5, "Links use a URL shortener, which hides the destination", string.Join(", ", shortened)));
        var mismatched = links
            .Where(l => l.Kind == "link")
            .Select(l => (Shown: LinkExtractor.HostInText(l.Text), Real: LinkExtractor.HttpHost(l.Url), l.Text))
            .FirstOrDefault(x => x.Shown is not null && x.Real is not null && !SameOrgDomain(x.Shown, x.Real));
        if (mismatched.Shown is not null)
            hits.Add(new("LINK_TEXT_MISMATCH", 2.0, "Link text shows one domain but points to another (a classic phishing pattern)", $"\"{mismatched.Text}\" → {mismatched.Real}"));
        var local = links.Select(l => LinkExtractor.HttpHost(l.Url)).OfType<string>().Where(HostClassifier.IsLocalOrPrivate).Distinct().ToList();
        if (local.Count > 0)
            hits.Add(new("LOCAL_URL", 1.0, "Links point at localhost or a private network; a development URL leaked into the email", string.Join(", ", local.Take(5))));
        var insecure = links.Count(l => l.Kind == "link" && l.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (insecure > 0)
            hits.Add(new("HTTP_LINKS", 0.3, "Links use http:// rather than https://", $"{insecure} link(s)"));

        // ---- attachments
        var risky = m.Attachments.Select(a => a is MimePart p ? p.FileName : null).OfType<string>()
            .Where(n => RiskyExtensions.Any(e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase))).ToList();
        if (risky.Count > 0)
            hits.Add(new("RISKY_ATTACHMENT", 3.0, "Executable or macro-enabled attachment; most providers block these", string.Join(", ", risky)));

        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Id, StringComparer.Ordinal).ToList();
    }

    public static string Verdict(double score) => score >= Threshold ? "Likely spam" : score >= 2 ? "Some issues" : "Looks clean";

    /// <summary>Rough "same organisation" test: equal, or one is a subdomain of the other's last two labels.</summary>
    public static bool SameOrgDomain(string a, string b)
    {
        static string Root(string d) { var p = d.TrimEnd('.').Split('.'); return p.Length <= 2 ? d : string.Join('.', p[^2..]); }
        return string.Equals(Root(a), Root(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Domain(string? address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1 ? null : address[(at + 1)..].Trim('>', ' ').ToLowerInvariant();
    }

    private static string VisibleText(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        foreach (var junk in doc.QuerySelectorAll("style, script, head").ToList()) junk.Remove();
        return WhitespaceRx().Replace(doc.Body?.TextContent ?? "", " ").Trim();
    }

    private static string Trim(string s)
    {
        s = WhitespaceRx().Replace(s, " ").Trim();
        return s.Length > 80 ? s[..77] + "…" : s;
    }

    [GeneratedRegex(@"[!?$]{3,}|!\s*!\s*!")] private static partial Regex ExcessPunctRx();
    [GeneratedRegex(@"display\s*:\s*none|font-size\s*:\s*0(px|pt|em|rem)?\s*(;|$)|visibility\s*:\s*hidden|max-height\s*:\s*0", RegexOptions.IgnoreCase)] private static partial Regex HiddenStyleRx();
    [GeneratedRegex(@"unsubscribe|opt[\s-]?out", RegexOptions.IgnoreCase)] private static partial Regex UnsubscribeRx();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRx();
}
