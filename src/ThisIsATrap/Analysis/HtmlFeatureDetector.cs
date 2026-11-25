using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ThisIsATrap.Analysis;

/// <summary>A caniemail feature found in a message, how often, and an example of where.</summary>
public sealed record FeatureUse(string Slug, int Count, string Sample);

/// <summary>
/// Finds the HTML and CSS features an email uses, as caniemail.com slugs. Only slugs that exist in
/// the support data are reported, so a heuristic miss can never invent a feature.
/// CSS is scanned with a tolerant tokenizer rather than a real CSS parser, because real parsers
/// drop vendor-specific and unknown properties, which are exactly what email clients choke on.
/// </summary>
public static partial class HtmlFeatureDetector
{
    private static readonly HashSet<string> Semantic = ["article", "aside", "details", "figcaption", "figure", "footer", "header", "main", "mark", "nav", "section", "summary", "time"];
    private static readonly HashSet<string> Lists = ["ul", "ol", "li", "dl", "dt", "dd"];
    private static readonly HashSet<string> InputTypes = ["checkbox", "radio", "hidden", "reset", "submit", "text"];
    private static readonly HashSet<string> Attributes = ["align", "background", "cellpadding", "cellspacing", "dir", "height", "hidden", "lang", "required", "role", "srcset", "target", "valign", "width", "popover"];

    public static Dictionary<string, FeatureUse> Detect(string html, IReadOnlyDictionary<string, SupportFeature> known)
    {
        var found = new Dictionary<string, FeatureUse>(StringComparer.Ordinal);
        void Add(string slug, string sample)
        {
            if (!known.ContainsKey(slug)) return;
            found[slug] = found.TryGetValue(slug, out var f) ? f with { Count = f.Count + 1 } : new FeatureUse(slug, 1, Trim(sample));
        }

        if (DoctypeRx().IsMatch(html)) Add("html-doctype", DoctypeRx().Match(html).Value);

        var doc = new HtmlParser().ParseDocument(html);

        foreach (var node in doc.Descendants())
        {
            if (node is IComment comment)
            {
                // Outlook conditional comments ("[if mso]") are a different, well-supported technique.
                if (!comment.Data.TrimStart().StartsWith("[if", StringComparison.OrdinalIgnoreCase))
                    Add("html-comments", "<!--" + comment.Data + "-->");
                continue;
            }
            if (node is not IElement el) continue;
            var tag = el.LocalName;
            // The parser invents <html>, <head>, <body> (and <tbody>) when the source omits them.
            if (tag is "html" or "head" or "body" or "tbody" && !Regex.IsMatch(html, "<" + tag + @"[\s>/]", RegexOptions.IgnoreCase)) continue;
            var sample = OpeningTag(el);

            switch (tag)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6": Add("html-h1-h6", sample); break;
                case "b" or "strong": Add("html-strong", sample); break;
                case "s" or "strike": Add("html-strike", sample); break;
                case "map" or "area": Add("html-image-maps", sample); break;
                case "svg": Add("html-svg", sample); break;
                case "input":
                    var type = (el.GetAttribute("type") ?? "text").ToLowerInvariant();
                    if (InputTypes.Contains(type)) Add("html-input-" + type, sample);
                    break;
                case "button":
                    var bt = (el.GetAttribute("type") ?? "submit").ToLowerInvariant();
                    if (bt is "submit" or "reset") Add("html-button-" + bt, sample);
                    break;
                case "meta" when string.Equals(el.GetAttribute("name"), "color-scheme", StringComparison.OrdinalIgnoreCase):
                    Add("html-meta-color-scheme", sample); break;
                case "a":
                    var href = el.GetAttribute("href") ?? "";
                    if (href.StartsWith('#') && href.Length > 1) Add("html-anchor-links", sample);
                    else if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) Add("html-mailto-links", sample);
                    break;
                case "style":
                    Add("html-style", sample);
                    ScanCss(el.TextContent, isStylesheet: true, Add);
                    break;
                case "html" when el.HasAttribute("amp") || el.HasAttribute("⚡") || el.HasAttribute("⚡4email"):
                    Add("amp", sample); break;
            }
            if (Semantic.Contains(tag)) Add("html-semantics", sample);
            if (Lists.Contains(tag)) Add("html-lists", sample);
            if (tag is not ("input" or "button" or "svg" or "style")) Add("html-" + tag, sample);

            foreach (var attr in el.Attributes)
            {
                var name = attr.Name.ToLowerInvariant();
                if (Attributes.Contains(name)) Add("html-" + name, sample);
                else if (name.StartsWith("aria-", StringComparison.Ordinal)) Add("html-" + name, sample);
                else if (name == "loading") Add("html-loading-attribute", sample);
                else if (name is "command" or "commandfor") Add("html-command-attribute", sample);
                else if (name == "inert") Add("css-inert-attribute", sample);
                else if (name == "style") ScanCss(attr.Value, isStylesheet: false, Add);
            }

            if (tag is "img" or "source")
            {
                foreach (var src in new[] { el.GetAttribute("src"), el.GetAttribute("srcset") })
                    if (!string.IsNullOrEmpty(src)) ImageFormat(src, sample, Add);
            }
            if (el.GetAttribute("background") is { Length: > 0 } bg) ImageFormat(bg, sample, Add);
        }
        return found;
    }

    // ------------------------------------------------------------------ CSS

    private static void ScanCss(string css, bool isStylesheet, Action<string, string> add)
    {
        foreach (Match c in CssCommentRx().Matches(css)) add("css-comments", c.Value);
        css = CssCommentRx().Replace(css, " ");

        if (css.Contains("!important", StringComparison.OrdinalIgnoreCase)) add("css-important", FirstLineWith(css, "!important"));

        if (isStylesheet)
        {
            foreach (Match at in AtRuleRx().Matches(css))
            {
                var name = at.Groups[1].Value.ToLowerInvariant();
                var prelude = at.Groups[2].Value;
                add("css-at-" + name, "@" + name + " " + prelude.Trim());
                if (name == "media")
                {
                    var p = prelude.ToLowerInvariant();
                    if (p.Contains("prefers-color-scheme")) add("css-at-media-prefers-color-scheme", "@media " + prelude.Trim());
                    if (p.Contains("prefers-reduced-motion")) add("css-at-media-prefers-reduced-motion", "@media " + prelude.Trim());
                    if (p.Contains("orientation")) add("css-at-media-orientation", "@media " + prelude.Trim());
                    if (HoverRx().IsMatch(p)) add("css-at-media-hover", "@media " + prelude.Trim());
                    if (p.Contains("device-pixel-ratio") || p.Contains("resolution")) add("css-at-media-device-pixel-ratio", "@media " + prelude.Trim());
                }
            }
            foreach (Match rule in RulePreludeRx().Matches(css))
            {
                var selector = rule.Groups[1].Value.Trim();
                if (selector.Length == 0 || selector.StartsWith('@') || PercentKeyframeRx().IsMatch(selector)) continue;
                ScanSelector(selector, add);
            }
            // A '{' that opens inside a declaration block (CSS nesting).
            if (NestingRx().IsMatch(css)) add("css-nesting", NestingRx().Match(css).Value);
        }

        // Declarations: a style="" attribute is one block; in a stylesheet only text inside { } counts
        // (so "max-width: 600px" in a media query isn't mistaken for a property).
        var blocks = isStylesheet ? BlockRx().Matches(css).Select(m => m.Groups[1].Value) : [css];
        foreach (var block in blocks)
        {
            foreach (Match d in DeclarationRx().Matches(block))
            {
                var prop = d.Groups[1].Value.ToLowerInvariant();
                var value = d.Groups[2].Value.Trim();
                ScanDeclaration(prop, value, prop + ": " + value, add);
            }
        }
    }

    private static void ScanSelector(string selector, Action<string, string> add)
    {
        if (selector.Contains(',')) add("css-selector-grouping", selector);
        foreach (var part in selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (Match pe in PseudoElementRx().Matches(part)) add("css-pseudo-element-" + pe.Groups[1].Value.ToLowerInvariant(), part);
            foreach (Match pc in PseudoClassRx().Matches(part)) add("css-pseudo-class-" + pc.Groups[1].Value.ToLowerInvariant(), part);
            if (part.Contains('#')) add("css-selector-id", part);
            if (ClassRx().IsMatch(part)) add("css-selector-class", part);
            if (part.Contains('[')) add("css-selector-attribute", part);
            if (part.Contains('>')) add("css-selector-child", part);
            if (part.Contains('+')) add("css-selector-adjacent-sibling", part);
            if (part.Contains('~')) add("css-selector-general-sibling", part);
            if (part.Contains('*')) add("css-selector-universal", part);
            var compounds = CombinatorRx().Split(part).Where(s => s.Length > 0).ToList();
            if (compounds.Count > 1 && !part.Contains('>') && !part.Contains('+') && !part.Contains('~')) add("css-selector-descendant", part);
            foreach (var compound in compounds)
            {
                if (TypeSelectorRx().IsMatch(compound)) add("css-selector-type", part);
                if (ChainRx().IsMatch(compound)) add("css-selector-chaining", part);
            }
        }
    }

    private static void ScanDeclaration(string prop, string value, string sample, Action<string, string> add)
    {
        if (prop.StartsWith("--", StringComparison.Ordinal)) { add("css-variables", sample); return; }
        var bare = VendorRx().Replace(prop, "");

        add("css-" + bare, sample);
        var firstValue = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (bare == "display" && firstValue is "inline-flex") add("css-display-flex", sample);
        if (bare == "display" && firstValue is "inline-grid") add("css-display-grid", sample);
        add("css-" + bare + "-" + firstValue, sample);

        var group = bare switch
        {
            "margin-top" or "margin-right" or "margin-bottom" or "margin-left" => "css-margin",
            "padding-top" or "padding-right" or "padding-bottom" or "padding-left" => "css-padding",
            "margin-inline-start" or "margin-inline-end" => "css-margin-inline-start-end",
            "margin-block-start" or "margin-block-end" => "css-margin-block-start-end",
            "margin-inline" or "margin-block" => "css-margin-inline-block",
            "padding-inline-start" or "padding-inline-end" => "css-padding-inline-start-end",
            "padding-block-start" or "padding-block-end" => "css-padding-block-start-end",
            "padding-inline" or "padding-block" => "css-padding-inline-block",
            "border-inline" or "border-block" => "css-border-inline-block",
            "border-start-start-radius" or "border-start-end-radius" or "border-end-start-radius" or "border-end-end-radius" => "css-border-radius-logical",
            "inline-size" or "block-size" => "css-block-inline-size",
            "row-gap" or "column-gap" => "css-gap",
            "left" or "right" or "top" or "bottom" => "css-left-right-top-bottom",
            "overflow-x" or "overflow-y" => "css-overflow",
            "word-wrap" => "css-word-wrap",
            _ when bare.StartsWith("border-inline-", StringComparison.Ordinal) || bare.StartsWith("border-block-", StringComparison.Ordinal) => "css-border-inline-block-longhand",
            _ when bare.EndsWith("-radius", StringComparison.Ordinal) => "css-border-radius",
            "border-top" or "border-right" or "border-bottom" or "border-left" or "border-width" or "border-style" or "border-color"
                or "border-top-width" or "border-top-style" or "border-top-color" or "border-right-width" or "border-right-style" or "border-right-color"
                or "border-bottom-width" or "border-bottom-style" or "border-bottom-color" or "border-left-width" or "border-left-style" or "border-left-color" => "css-border",
            _ when bare.StartsWith("column-", StringComparison.Ordinal) || bare is "columns" => "css-column-layout-properties",
            _ when bare.StartsWith("grid-template", StringComparison.Ordinal) => "css-grid-template",
            _ when bare.StartsWith("scroll-snap", StringComparison.Ordinal) => "css-scroll-snap",
            _ when bare.StartsWith("transition", StringComparison.Ordinal) => "css-transition",
            _ when bare.StartsWith("animation", StringComparison.Ordinal) => "css-animation",
            _ when bare.StartsWith("font-", StringComparison.Ordinal) && bare is not ("font-size" or "font-weight" or "font-stretch" or "font-kerning" or "font-size-adjust") => null,
            _ => null,
        };
        if (group is not null) add(group, sample);

        var v = value.ToLowerInvariant();
        if (v.Contains("var(")) add("css-variables", sample);
        if (v.Contains("linear-gradient(")) add("css-linear-gradient", sample);
        if (v.Contains("radial-gradient(")) add("css-radial-gradient", sample);
        if (v.Contains("conic-gradient(")) add("css-conic-gradient", sample);
        if (RgbRx().IsMatch(v)) add("css-rgb", sample);
        if (v.Contains("rgba(")) add("css-rgba", sample);
        if (ModernColorRx().IsMatch(v)) add("css-modern-color", sample);
        if (v.Contains("calc(")) add("css-unit-calc", sample);
        if (v.Contains("clamp(")) add("css-function-clamp", sample);
        if (MinRx().IsMatch(v)) add("css-function-min", sample);
        if (MaxRx().IsMatch(v)) add("css-function-max", sample);
        if (v.Contains("light-dark(")) add("css-function-light-dark", sample);
        if (v.Contains("system-ui")) add("css-sytem-ui", sample); // sic: caniemail's slug
        if (v.Trim() == "initial") add("css-unit-initial", sample);
        foreach (Match u in UnitRx().Matches(v)) add("css-unit-" + (u.Groups[1].Value == "%" ? "percent" : u.Groups[1].Value), sample);
        foreach (Match url in UrlRx().Matches(value)) ImageFormat(url.Groups[1].Value, sample, add);
    }

    private static void ImageFormat(string src, string sample, Action<string, string> add)
    {
        src = src.Trim().Trim('"', '\'');
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            add("image-base64", sample);
            var mime = src[5..].Split(';', ',')[0].ToLowerInvariant();
            if (mime.StartsWith("image/", StringComparison.Ordinal)) add("image-" + NormalizeExt(mime[6..]), sample);
            return;
        }
        foreach (var candidate in src.Split(',')) // srcset lists
        {
            var path = candidate.Trim().Split(' ')[0].Split('?', '#')[0];
            var dot = path.LastIndexOf('.');
            if (dot < 0 || dot < path.LastIndexOf('/')) continue;
            add("image-" + NormalizeExt(path[(dot + 1)..].ToLowerInvariant()), sample);
        }
    }

    private static string NormalizeExt(string ext) => ext switch
    {
        "jpeg" or "jpe" => "jpg",
        "svg+xml" => "svg",
        "x-icon" or "vnd.microsoft.icon" => "ico",
        "tif" => "tiff",
        "heic" => "heif",
        _ => ext,
    };

    private static string OpeningTag(IElement el)
    {
        var html = el.OuterHtml;
        var end = html.IndexOf('>');
        return end > 0 ? html[..(end + 1)] : html;
    }

    private static string FirstLineWith(string text, string needle)
    {
        var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, text.LastIndexOfAny([';', '{', '\n'], Math.Max(0, i - 1)) + 1);
        return text[start..Math.Min(text.Length, i + needle.Length)].Trim();
    }

    private static string Trim(string s)
    {
        s = WhitespaceRx().Replace(s, " ").Trim();
        return s.Length > 120 ? s[..117] + "…" : s;
    }

    [GeneratedRegex(@"<!doctype[^>]*>", RegexOptions.IgnoreCase)] private static partial Regex DoctypeRx();
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)] private static partial Regex CssCommentRx();
    [GeneratedRegex(@"@(media|font-face|import|keyframes|supports)\b([^{;]*)", RegexOptions.IgnoreCase)] private static partial Regex AtRuleRx();
    [GeneratedRegex(@"\(\s*(any-)?hover\b")] private static partial Regex HoverRx();
    [GeneratedRegex(@"(?:^|[{}])\s*([^{}@;][^{};]*?)\s*\{")] private static partial Regex RulePreludeRx();
    [GeneratedRegex(@"^(from|to|\d+%)(\s*,\s*(from|to|\d+%))*$")] private static partial Regex PercentKeyframeRx();
    [GeneratedRegex(@"\{[^{}]*;\s*[^{};]*&?[\w.#:\[\]-]+[^{};]*\{")] private static partial Regex NestingRx();
    [GeneratedRegex(@"::?(before|after|first-letter|first-line|marker|placeholder)\b", RegexOptions.IgnoreCase)] private static partial Regex PseudoElementRx();
    [GeneratedRegex(@"(?<!:):(active|checked|default|first-child|first-of-type|focus-visible|focus-within|focus|has|hover|lang|last-child|last-of-type|link|not|nth-child|nth-last-child|nth-last-of-type|nth-of-type|only-child|only-of-type|target|visited)\b", RegexOptions.IgnoreCase)] private static partial Regex PseudoClassRx();
    [GeneratedRegex(@"\.[A-Za-z_-]")] private static partial Regex ClassRx();
    [GeneratedRegex(@"\s*[>+~]\s*|\s+")] private static partial Regex CombinatorRx();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*")] private static partial Regex TypeSelectorRx();
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9]*|\*)?([.#\[][^.#\[]+){2,}|^[A-Za-z][A-Za-z0-9]*[.#\[]")] private static partial Regex ChainRx();
    [GeneratedRegex(@"(?<![\w-])(-{0,2}[A-Za-z][\w-]*)\s*:\s*([^;{}]+)")] private static partial Regex DeclarationRx();
    [GeneratedRegex(@"\{([^{}]*)\}")] private static partial Regex BlockRx();
    [GeneratedRegex(@"^-(webkit|moz|ms|o)-")] private static partial Regex VendorRx();
    [GeneratedRegex(@"(?<![\w-])rgb\(")] private static partial Regex RgbRx();
    [GeneratedRegex(@"(?<![\w-])(hsla?|hwb|lab|lch|oklab|oklch|color)\(")] private static partial Regex ModernColorRx();
    [GeneratedRegex(@"(?<![\w-])min\(")] private static partial Regex MinRx();
    [GeneratedRegex(@"(?<![\w-])max\(")] private static partial Regex MaxRx();
    [GeneratedRegex(@"(?<![\w.#-])-?\d*\.?\d+(px|rem|em|ex|ch|vh|vw|vmin|vmax|cm|mm|in|pt|pc|%)(?![\w-])")] private static partial Regex UnitRx();
    [GeneratedRegex(@"url\(\s*([^)]+?)\s*\)", RegexOptions.IgnoreCase)] private static partial Regex UrlRx();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRx();
}
