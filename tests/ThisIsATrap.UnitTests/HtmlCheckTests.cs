using System.Text;
using ThisIsATrap.Analysis;

namespace ThisIsATrap.Tests;

public sealed class HtmlFeatureDetectorTests
{
    private static readonly IReadOnlyDictionary<string, SupportFeature> Known = SupportData.Default.BySlug;

    private static Dictionary<string, FeatureUse> Detect(string html) => HtmlFeatureDetector.Detect(html, Known);

    [Fact]
    public void Bundled_data_loads_with_hundreds_of_features()
    {
        var data = SupportData.Default;
        Assert.True(data.Features.Count > 250);
        Assert.Contains("MIT", data.Source);
        Assert.Equal("Gmail", data.Families["gmail"]);
        Assert.NotEmpty(Known["css-display-flex"].Stats["outlook"]);
    }

    [Theory]
    [InlineData("<div style=\"display:flex\">x</div>", "css-display-flex")]
    [InlineData("<div style=\"display: inline-flex\">x</div>", "css-display-flex")]
    [InlineData("<div style=\"display:grid\">x</div>", "css-display-grid")]
    [InlineData("<div style=\"box-shadow:0 0 2px #000\">x</div>", "css-box-shadow")]
    [InlineData("<div style=\"-webkit-box-shadow:0 0 2px #000\">x</div>", "css-box-shadow")]
    [InlineData("<div style=\"margin-top:4px\">x</div>", "css-margin")]
    [InlineData("<div style=\"padding-inline-start:4px\">x</div>", "css-padding-inline-start-end")]
    [InlineData("<div style=\"border-top-left-radius:4px\">x</div>", "css-border-radius")]
    [InlineData("<div style=\"border-left:1px solid red\">x</div>", "css-border")]
    [InlineData("<div style=\"top:0\">x</div>", "css-left-right-top-bottom")]
    [InlineData("<div style=\"--brand:#123\">x</div>", "css-variables")]
    [InlineData("<div style=\"color:var(--brand)\">x</div>", "css-variables")]
    [InlineData("<div style=\"background:linear-gradient(red,blue)\">x</div>", "css-linear-gradient")]
    [InlineData("<div style=\"color:rgba(0,0,0,.5)\">x</div>", "css-rgba")]
    [InlineData("<div style=\"color:oklch(70% 0.1 200)\">x</div>", "css-modern-color")]
    [InlineData("<div style=\"width:calc(100% - 2px)\">x</div>", "css-unit-calc")]
    [InlineData("<div style=\"font-size:1.2rem\">x</div>", "css-unit-rem")]
    [InlineData("<div style=\"width:50%\">x</div>", "css-unit-percent")]
    [InlineData("<div style=\"font-family:system-ui\">x</div>", "css-sytem-ui")]
    [InlineData("<div style=\"color:red !important\">x</div>", "css-important")]
    public void Inline_css_features(string html, string slug) => Assert.Contains(slug, Detect(html).Keys);

    [Theory]
    [InlineData("@media (max-width:600px){.a{color:red}}", "css-at-media")]
    [InlineData("@media (prefers-color-scheme: dark){.a{color:red}}", "css-at-media-prefers-color-scheme")]
    [InlineData("@media (hover: hover){.a{color:red}}", "css-at-media-hover")]
    [InlineData("@font-face{font-family:X;src:url(x.woff)}", "css-at-font-face")]
    [InlineData("@import url(x.css);", "css-at-import")]
    [InlineData("@keyframes spin{from{opacity:0}to{opacity:1}}", "css-at-keyframes")]
    [InlineData("a:hover{color:red}", "css-pseudo-class-hover")]
    [InlineData("li:nth-child(2n){color:red}", "css-pseudo-class-nth-child")]
    [InlineData("p::before{content:'x'}", "css-pseudo-element-before")]
    [InlineData("#main{color:red}", "css-selector-id")]
    [InlineData(".btn{color:red}", "css-selector-class")]
    [InlineData("a[href]{color:red}", "css-selector-attribute")]
    [InlineData("ul > li{color:red}", "css-selector-child")]
    [InlineData("h1 + p{color:red}", "css-selector-adjacent-sibling")]
    [InlineData("h1 ~ p{color:red}", "css-selector-general-sibling")]
    [InlineData("table td{color:red}", "css-selector-descendant")]
    [InlineData("h1, h2{color:red}", "css-selector-grouping")]
    [InlineData("*{margin:0}", "css-selector-universal")]
    [InlineData("a.btn{color:red}", "css-selector-chaining")]
    [InlineData("/* note */ p{color:red}", "css-comments")]
    public void Stylesheet_features(string css, string slug) =>
        Assert.Contains(slug, Detect($"<html><head><style>{css}</style></head><body><p>x</p></body></html>").Keys);

    [Fact]
    public void Media_query_conditions_are_not_counted_as_properties()
    {
        var found = Detect("<style>@media (max-width:600px){.a{color:red}}</style><p>x</p>");
        Assert.DoesNotContain("css-max-width", found.Keys);
        Assert.Contains("css-at-media", found.Keys);
    }

    [Theory]
    [InlineData("<svg width=\"10\"></svg>", "html-svg")]
    [InlineData("<video src=\"a.mp4\"></video>", "html-video")]
    [InlineData("<h2>t</h2>", "html-h1-h6")]
    [InlineData("<ul><li>a</li></ul>", "html-lists")]
    [InlineData("<b>x</b>", "html-strong")]
    [InlineData("<strike>x</strike>", "html-strike")]
    [InlineData("<section>x</section>", "html-semantics")]
    [InlineData("<form><input type=\"checkbox\"></form>", "html-input-checkbox")]
    [InlineData("<form><input></form>", "html-input-text")]
    [InlineData("<button>x</button>", "html-button-submit")]
    [InlineData("<a href=\"#top\">x</a>", "html-anchor-links")]
    [InlineData("<a href=\"mailto:a@b.c\">x</a>", "html-mailto-links")]
    [InlineData("<table cellpadding=\"0\"><tr><td valign=\"top\">x</td></tr></table>", "html-cellpadding")]
    [InlineData("<table><tr><td valign=\"top\">x</td></tr></table>", "html-valign")]
    [InlineData("<div role=\"presentation\">x</div>", "html-role")]
    [InlineData("<div aria-label=\"x\">x</div>", "html-aria-label")]
    [InlineData("<img src=\"a.png\" loading=\"lazy\">", "html-loading-attribute")]
    [InlineData("<img srcset=\"a.png 1x, a@2x.png 2x\">", "html-srcset")]
    [InlineData("<map name=\"m\"><area href=\"x\"></map>", "html-image-maps")]
    [InlineData("<!-- tracking --><p>x</p>", "html-comments")]
    [InlineData("<!DOCTYPE html><p>x</p>", "html-doctype")]
    [InlineData("<meta name=\"color-scheme\" content=\"light dark\"><p>x</p>", "html-meta-color-scheme")]
    [InlineData("<html ⚡4email><body>x</body></html>", "amp")]
    public void Html_features(string html, string slug) => Assert.Contains(slug, Detect(html).Keys);

    [Theory]
    [InlineData("<img src=\"logo.svg\">", "image-svg")]
    [InlineData("<img src=\"https://x/hero.WEBP?v=2\">", "image-webp")]
    [InlineData("<img src=\"photo.jpeg\">", "image-jpg")]
    [InlineData("<img src=\"anim.gif\">", "image-gif")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">", "image-base64")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">", "image-png")]
    [InlineData("<div style=\"background-image:url('bg.avif')\">x</div>", "image-avif")]
    [InlineData("<table><tr><td background=\"bg.png\">x</td></tr></table>", "image-png")]
    public void Image_formats(string html, string slug) => Assert.Contains(slug, Detect(html).Keys);

    [Fact]
    public void Implicit_document_elements_are_not_reported()
    {
        var found = Detect("<p>Just a paragraph</p><table><tr><td>x</td></tr></table>");
        Assert.DoesNotContain("html-body", found.Keys);
        Assert.Contains("html-p", found.Keys);
        Assert.Contains("html-table", found.Keys);
    }

    [Fact]
    public void Outlook_conditional_comments_are_not_html_comments()
    {
        var found = Detect("<!--[if mso]><table><tr><td><![endif]--><p>x</p>");
        Assert.DoesNotContain("html-comments", found.Keys);
    }

    [Fact]
    public void Unknown_features_are_never_reported_and_counts_accumulate()
    {
        var found = Detect("<p style=\"made-up-prop:1\">a</p><p>b</p><p>c</p>");
        Assert.All(found.Keys, k => Assert.True(Known.ContainsKey(k)));
        Assert.Equal(3, found["html-p"].Count);
        Assert.Equal("<p style=\"made-up-prop:1\">", found["html-p"].Sample);
    }

    [Fact]
    public void Long_samples_are_truncated()
    {
        var found = Detect($"<div style=\"color:red;{new string('x', 300)}:1\">x</div>");
        Assert.True(found["html-div"].Sample.Length <= 120);
    }
}

public sealed class HtmlCheckScoringTests
{
    private static SupportData Data(string json) => SupportData.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    // Two clients; feature A is fully supported, feature B is unsupported in Outlook and partial (with a note) in Gmail.
    private static readonly SupportData Small = Data("""
        {
          "source": "test", "lastUpdate": "2026-01-01",
          "families": { "gmail": "Gmail", "outlook": "Outlook" },
          "platforms": { "desktop-webmail": "Desktop Webmail", "windows": "Windows", "ios": "iOS" },
          "features": [
            { "slug": "html-p", "title": "<p>", "category": "html", "url": "u1", "notes": {},
              "stats": { "gmail": { "desktop-webmail": "y" }, "outlook": { "windows": "y" } } },
            { "slug": "css-box-shadow", "title": "box-shadow", "category": "css", "url": "u2", "notes": { "1": "Only on iOS." },
              "stats": { "gmail": { "desktop-webmail": "n", "ios": "a #1" }, "outlook": { "windows": "n", "ios": "u" } } }
          ]
        }
        """);

    [Fact]
    public void Scores_features_clients_and_overall()
    {
        var report = new HtmlCheck(Small).Run("<p style=\"box-shadow:0 0 1px #000\">x</p>");

        Assert.True(report.HasHtml);
        Assert.Equal(2, report.Features.Count);
        var shadow = report.Features[0]; // least supported first
        Assert.Equal("css-box-shadow", shadow.Slug);
        Assert.Equal(16.7, shadow.Score);      // n, a(0.5), n; unknown ignored → 0.5 / 3
        Assert.Equal(100, report.Features[1].Score);
        Assert.Equal(58.4, report.Score);      // mean of 16.7 and 100, rounded

        var gmail = shadow.Clients.Single(c => c.Family == "gmail");
        Assert.Equal("a", gmail.Support);
        Assert.Equal(["Only on iOS."], gmail.Platforms.Single(p => p.Platform == "ios").Notes);
        Assert.Equal("n", shadow.Clients.Single(c => c.Family == "outlook").Support);

        var outlook = report.Clients.Single(c => c.Family == "outlook");
        Assert.Equal(50, outlook.Score);       // p: y, box-shadow: n
        Assert.Equal(1, outlook.Unsupported);
        Assert.Equal(75, report.Clients.Single(c => c.Family == "gmail").Score);
    }

    [Fact]
    public void Message_without_html_scores_100_with_no_features()
    {
        var report = new HtmlCheck(Small).Run(null);
        Assert.False(report.HasHtml);
        Assert.Equal(100, report.Score);
        Assert.Empty(report.Features);
    }

    [Theory]
    [InlineData("y", "y")]
    [InlineData("a #1 #2", "a")]
    [InlineData("n", "n")]
    [InlineData("u", "u")]
    [InlineData("", "u")]
    [InlineData("weird", "u")]
    public void Parses_support_values(string raw, string expected) => Assert.Equal(expected, HtmlCheck.ParseValue(raw).Support);

    [Fact]
    public void Parses_note_references() => Assert.Equal(["1", "2"], HtmlCheck.ParseValue("a #1 #2").Notes);

    [Theory]
    [InlineData(new[] { "y", "y" }, "y")]
    [InlineData(new[] { "n", "n", "u" }, "n")]
    [InlineData(new[] { "y", "n" }, "a")]
    [InlineData(new[] { "a" }, "a")]
    [InlineData(new[] { "u" }, "u")]
    [InlineData(new string[0], "u")]
    public void Combines_platforms_into_client_support(string[] values, string expected) => Assert.Equal(expected, HtmlCheck.Combine(values));

    [Fact]
    public void Real_data_flags_flexbox_as_poorly_supported_in_outlook()
    {
        var report = new HtmlCheck(SupportData.Default).Run("<div style=\"display:flex\">x</div>");
        var flex = report.Features.Single(f => f.Slug == "css-display-flex");
        Assert.Equal("a", flex.Clients.Single(c => c.Family == "outlook").Support); // Windows: n, others: y
        Assert.True(flex.Score < 100);
        Assert.Equal(HtmlCheck.MajorFamilies[0], report.Clients[0].Family);
    }
}
