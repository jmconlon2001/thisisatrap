using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThisIsATrap.Analysis;

/// <summary>One caniemail.com feature with the latest support value per client family and platform.</summary>
public sealed class SupportFeature
{
    public string Slug { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
    public string Url { get; init; } = "";
    public Dictionary<string, string> Notes { get; init; } = [];

    /// <summary>family → platform → value such as "y", "n", "a #1", "u".</summary>
    public Dictionary<string, Dictionary<string, string>> Stats { get; init; } = [];
}

/// <summary>The bundled Can I email dataset (MIT License, https://github.com/hteumeuleu/caniemail).</summary>
public sealed class SupportData
{
    public string Source { get; init; } = "";
    public string LastUpdate { get; init; } = "";
    public Dictionary<string, string> Families { get; init; } = [];
    public Dictionary<string, string> Platforms { get; init; } = [];
    public List<SupportFeature> Features { get; init; } = [];

    [JsonIgnore] public IReadOnlyDictionary<string, SupportFeature> BySlug => _bySlug ??= Features.ToDictionary(f => f.Slug, StringComparer.Ordinal);
    private Dictionary<string, SupportFeature>? _bySlug;

    private static readonly Lazy<SupportData> Bundled = new(() =>
    {
        using var stream = typeof(SupportData).Assembly.GetManifestResourceStream("ThisIsATrap.Data.caniemail.json")
            ?? throw new InvalidOperationException("Embedded caniemail.json is missing.");
        return Parse(stream);
    });

    public static SupportData Default => Bundled.Value;

    public static SupportData Parse(Stream json) =>
        JsonSerializer.Deserialize<SupportData>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("Invalid support data.");
}
