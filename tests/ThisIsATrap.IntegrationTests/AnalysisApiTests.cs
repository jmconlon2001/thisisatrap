using System.Net;
using ThisIsATrap.Tests;

namespace ThisIsATrap.IntegrationTests;

[Collection(AppCollection.Name)]
public sealed class AnalysisApiTests(TrapApp app)
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> Send(string? html, string? text = "Plain body")
    {
        var subject = "analysis-" + Guid.NewGuid();
        await app.SendAsync(TestMail.Build(subject: subject, html: html, text: text));
        return (await app.FindAsync(subject)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Html_check_reports_features_clients_and_score()
    {
        var id = await Send("<html><head><style>@media (max-width:600px){.a{display:block}}</style></head><body><div style=\"display:flex;box-shadow:0 0 2px #000\">x</div></body></html>");

        var r = await app.GetJsonAsync($"/api/messages/{id}/html-check");

        Assert.True(r.GetProperty("hasHtml").GetBoolean());
        var slugs = r.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("slug").GetString()).ToList();
        Assert.Contains("css-display-flex", slugs);
        Assert.Contains("css-box-shadow", slugs);
        Assert.Contains("css-at-media", slugs);
        Assert.InRange(r.GetProperty("score").GetDouble(), 1, 99.9);
        Assert.Equal("apple-mail", r.GetProperty("clients")[0].GetProperty("family").GetString());
        Assert.StartsWith("https://www.caniemail.com/", r.GetProperty("features")[0].GetProperty("url").GetString());

        // Second call is served from the cache and is identical.
        var again = await app.GetJsonAsync($"/api/messages/{id}/html-check");
        Assert.Equal(r.GetRawText(), again.GetRawText());
    }

    [Fact]
    public async Task Html_check_of_text_only_mail_is_empty()
    {
        var id = await Send(html: null);
        var r = await app.GetJsonAsync($"/api/messages/{id}/html-check");
        Assert.False(r.GetProperty("hasHtml").GetBoolean());
        Assert.Equal(0, r.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task Html_check_for_unknown_message_is_404()
    {
        using var http = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/messages/{Guid.NewGuid():N}/html-check", Ct)).StatusCode);
    }
}
