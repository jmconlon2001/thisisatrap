namespace ThisIsATrap.Analysis;

public sealed record PlatformSupport(string Platform, string Name, string Support, IReadOnlyList<string> Notes);

public sealed record ClientFeatureSupport(string Family, string Name, string Support, IReadOnlyList<PlatformSupport> Platforms);

public sealed record FeatureResult(
    string Slug, string Title, string Category, string Url, int Occurrences, string Sample,
    double Score, IReadOnlyList<ClientFeatureSupport> Clients);

public sealed record ClientSummary(string Family, string Name, double Score, int Unsupported, int Partial, int Features);

public sealed record HtmlCheckReport(
    double Score, bool HasHtml, string DataUpdated, string Source,
    IReadOnlyList<ClientSummary> Clients, IReadOnlyList<FeatureResult> Features);

/// <summary>
/// Scores how well the HTML/CSS in a message is supported across email clients, the way
/// mailtrap.io's "HTML Check" does, using caniemail.com data.
/// Support values: y = supported, a = partial, n = not supported, u = unknown (ignored).
/// </summary>
public sealed class HtmlCheck(SupportData data)
{
    /// <summary>Clients shown in the summary, in order. Everything else is still in each feature's detail.</summary>
    public static readonly string[] MajorFamilies =
        ["apple-mail", "gmail", "outlook", "yahoo", "samsung-email", "thunderbird", "aol", "protonmail", "fastmail", "hey"];

    public HtmlCheckReport Run(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new HtmlCheckReport(100, false, data.LastUpdate, data.Source, [], []);

        var uses = HtmlFeatureDetector.Detect(html, data.BySlug);
        var features = uses.Values
            .Select(u => Evaluate(data.BySlug[u.Slug], u))
            .OrderBy(f => f.Score).ThenBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var clients = MajorFamilies
            .Where(data.Families.ContainsKey)
            .Select(family =>
            {
                var values = features
                    .Select(f => f.Clients.FirstOrDefault(c => c.Family == family)?.Support)
                    .Where(s => s is "y" or "a" or "n")
                    .ToList();
                return new ClientSummary(family, data.Families[family],
                    values.Count == 0 ? 100 : Math.Round(values.Average(Points) * 100, 1),
                    values.Count(s => s == "n"), values.Count(s => s == "a"), values.Count);
            })
            .ToList();

        var score = features.Count == 0 ? 100 : Math.Round(features.Average(f => f.Score), 1);
        return new HtmlCheckReport(score, true, data.LastUpdate, data.Source, clients, features);
    }

    private FeatureResult Evaluate(SupportFeature feature, FeatureUse use)
    {
        var clients = new List<ClientFeatureSupport>();
        var points = new List<double>();
        foreach (var (family, platforms) in feature.Stats)
        {
            var list = new List<PlatformSupport>();
            foreach (var (platform, raw) in platforms)
            {
                var (support, noteKeys) = ParseValue(raw);
                var notes = noteKeys.Select(k => feature.Notes.TryGetValue(k, out var n) ? n : null).OfType<string>().ToList();
                list.Add(new PlatformSupport(platform, data.Platforms.GetValueOrDefault(platform, platform), support, notes));
                if (support is "y" or "a" or "n") points.Add(Points(support));
            }
            clients.Add(new ClientFeatureSupport(family, data.Families.GetValueOrDefault(family, family), Combine(list.Select(p => p.Support)), list));
        }

        // Major clients first, then the rest alphabetically.
        clients = clients
            .OrderBy(c => Array.IndexOf(MajorFamilies, c.Family) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var score = points.Count == 0 ? 100 : Math.Round(points.Average() * 100, 1);
        return new FeatureResult(feature.Slug, feature.Title, feature.Category, feature.Url, use.Count, use.Sample, score, clients);
    }

    /// <summary>"a #1 #3" → ("a", ["1", "3"]).</summary>
    public static (string Support, IReadOnlyList<string> Notes) ParseValue(string raw)
    {
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var support = parts.Length == 0 ? "u" : parts[0] switch { "y" or "a" or "n" => parts[0], _ => "u" };
        return (support, parts.Skip(1).Where(p => p.StartsWith('#')).Select(p => p[1..]).ToList());
    }

    /// <summary>All known platforms supported → y; none → n; a mix or any partial → a; nothing known → u.</summary>
    public static string Combine(IEnumerable<string> values)
    {
        var known = values.Where(v => v is "y" or "a" or "n").ToList();
        if (known.Count == 0) return "u";
        if (known.All(v => v == "y")) return "y";
        if (known.All(v => v == "n")) return "n";
        return "a";
    }

    private static double Points(string? support) => support switch { "y" => 1, "a" => 0.5, _ => 0 };
}
