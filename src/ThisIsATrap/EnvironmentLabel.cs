using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ThisIsATrap;

/// <summary>The environment badge shown in the header. Source: "settings" (saved from the UI), "config", or "none".</summary>
public sealed record EnvironmentBadge(string? Label, string Color, string Source);

/// <summary>What the Settings dialog saves. A null/empty label removes the override (config applies again).</summary>
public sealed record EnvironmentBadgeInput(string? Label, string? Color);

/// <summary>
/// Resolves the environment label: a value saved from the UI wins, otherwise ThisIsATrap:EnvironmentLabel
/// from configuration (handy as an env var per deployment: ThisIsATrap__EnvironmentLabel=QA).
/// </summary>
public sealed partial class EnvironmentLabels(MailStore store, IOptions<TrapOptions> options)
{
    public const string SettingKey = "environment";
    public const int MaxLength = 24;
    public const string DefaultColor = "indigo";
    public static readonly string[] NamedColors = ["red", "orange", "amber", "green", "teal", "blue", "indigo", "purple", "pink", "gray"];

    public EnvironmentBadge Get()
    {
        if (store.GetSetting<EnvironmentBadgeInput>(SettingKey) is { Label: { Length: > 0 } saved } s)
            return new EnvironmentBadge(saved, NormalizeColor(s.Color, saved), "settings");
        var o = options.Value;
        var label = Clean(o.EnvironmentLabel);
        return label is null ? new EnvironmentBadge(null, DefaultColor, "none") : new EnvironmentBadge(label, NormalizeColor(o.EnvironmentColor, label), "config");
    }

    public EnvironmentBadge Save(EnvironmentBadgeInput input)
    {
        var label = Clean(input.Label);
        if (input.Color is { Length: > 0 } c && !IsValidColor(c))
            throw new ArgumentException($"Unknown colour '{c}'. Use {string.Join(", ", NamedColors)} or #rgb / #rrggbb.");
        store.SaveSetting(SettingKey, new EnvironmentBadgeInput(label, label is null ? null : input.Color?.Trim().ToLowerInvariant()));
        return Get();
    }

    /// <summary>Trims, drops control characters, and limits the length. Empty → null.</summary>
    public static string? Clean(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var clean = new string(label.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (clean.Length > MaxLength) clean = clean[..MaxLength].Trim();
        return clean.Length == 0 ? null : clean;
    }

    public static bool IsValidColor(string color) =>
        NamedColors.Contains(color.Trim().ToLowerInvariant()) || HexRx().IsMatch(color.Trim());

    /// <summary>The given colour if valid; otherwise a sensible default from the label (prod-ish → red, stage/QA → amber, dev → green).</summary>
    public static string NormalizeColor(string? color, string label)
    {
        if (!string.IsNullOrWhiteSpace(color) && IsValidColor(color)) return color.Trim().ToLowerInvariant();
        var l = label.ToLowerInvariant();
        return l.Contains("prod") || l.Contains("live") ? "red"
            : l.Contains("stag") || l.Contains("qa") || l.Contains("uat") || l.Contains("test") ? "amber"
            : l.Contains("dev") || l.Contains("local") ? "green"
            : DefaultColor;
    }

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")] private static partial Regex HexRx();
}
