using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace ThisIsATrap.Analysis;

/// <summary>A URL found in a message. Kind: link, image, background, or text (a bare URL in the plain-text part).</summary>
public sealed record LinkRef(string Url, string Kind, string Text);

/// <summary>Pulls every link and image URL out of a message's HTML and plain-text parts.</summary>
public static partial class LinkExtractor
{
    public static List<LinkRef> Extract(string? html, string? text)
    {
        var links = new List<LinkRef>();
        if (!string.IsNullOrWhiteSpace(html))
        {
            var doc = new HtmlParser().ParseDocument(html);
            foreach (var a in doc.QuerySelectorAll("a[href], area[href]"))
                links.Add(new LinkRef(a.GetAttribute("href")!.Trim(), "link", Collapse(a.TextContent)));
            foreach (var img in doc.QuerySelectorAll("img[src]"))
                links.Add(new LinkRef(img.GetAttribute("src")!.Trim(), "image", img.GetAttribute("alt") ?? ""));
            foreach (var el in doc.QuerySelectorAll("[background]"))
                links.Add(new LinkRef(el.GetAttribute("background")!.Trim(), "background", el.LocalName));
            foreach (var el in doc.QuerySelectorAll("[style]"))
                foreach (Match m in CssUrlRx().Matches(el.GetAttribute("style")!))
                    links.Add(new LinkRef(m.Groups[1].Value.Trim().Trim('"', '\''), "background", el.LocalName));
            foreach (var style in doc.QuerySelectorAll("style"))
                foreach (Match m in CssUrlRx().Matches(style.TextContent))
                    links.Add(new LinkRef(m.Groups[1].Value.Trim().Trim('"', '\''), "background", "style"));
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            var seen = links.Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
            foreach (Match m in TextUrlRx().Matches(text))
                if (seen.Add(m.Value)) links.Add(new LinkRef(m.Value, "text", ""));
        }
        return links.Where(l => l.Url.Length > 0).ToList();
    }

    /// <summary>The host of an absolute http(s) URL, or null.</summary>
    public static string? HttpHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? u.IdnHost.ToLowerInvariant() : null;

    /// <summary>Text that itself looks like a URL or domain (e.g. "www.bank.com"), returning that host.</summary>
    public static string? HostInText(string text)
    {
        var m = TextHostRx().Match(text.Trim());
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string Collapse(string s) => WhitespaceRx().Replace(s, " ").Trim();

    [GeneratedRegex(@"url\(\s*([^)]+?)\s*\)", RegexOptions.IgnoreCase)] private static partial Regex CssUrlRx();
    [GeneratedRegex(@"https?://[^\s<>""'()]+[^\s<>""'().,;:!?]", RegexOptions.IgnoreCase)] private static partial Regex TextUrlRx();
    [GeneratedRegex(@"^(?:https?://)?((?:[a-z0-9-]+\.)+[a-z]{2,})(?:[/:?#]\S*)?$", RegexOptions.IgnoreCase)] private static partial Regex TextHostRx();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRx();
}

/// <summary>Recognises hosts that only make sense on a developer machine or private network.</summary>
public static class HostClassifier
{
    private static readonly string[] LocalSuffixes = [".localhost", ".local", ".internal", ".lan", ".home.arpa", ".corp"];

    /// <summary>localhost, *.local and friends, or an IP literal in a loopback/private/link-local range.</summary>
    public static bool IsLocalOrPrivate(string host)
    {
        host = host.Trim('[', ']').ToLowerInvariant();
        if (host is "localhost" || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal))) return true;
        return IPAddress.TryParse(host, out var ip) && IsPrivate(ip);
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // carrier-grade NAT
                || b[0] == 0;
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6Any);
    }

    /// <summary>169.254.0.0/16 and fd00:ec2::254: cloud instance-metadata endpoints. Never fetched.</summary>
    public static bool IsMetadata(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        return (ip.AddressFamily == AddressFamily.InterNetwork && b[0] == 169 && b[1] == 254)
            || ip.Equals(IPAddress.Parse("fd00:ec2::254"));
    }
}
