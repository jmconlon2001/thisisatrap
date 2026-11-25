namespace ThisIsATrap.Health;

/// <summary>
/// `dotnet ThisIsATrap.dll --health-check` asks the running instance for /readyz and exits 0 (ready) or 1.
/// Used by the Docker HEALTHCHECK, since the runtime image has no curl or wget.
/// </summary>
public static class HealthProbe
{
    public const string Argument = "--health-check";

    public static bool IsRequested(string[] args) => args.Contains(Argument, StringComparer.OrdinalIgnoreCase);

    /// <summary>The local URL of the readiness endpoint, from ASPNETCORE_HTTP_PORTS / ASPNETCORE_URLS.</summary>
    public static Uri ReadyUrl(Func<string, string?> env)
    {
        var port = env("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0].Trim();
        if (string.IsNullOrEmpty(port))
        {
            var url = env("ASPNETCORE_URLS")?.Split(';')[0].Trim();
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var u))
                port = u.Port.ToString();
        }
        return new Uri($"http://127.0.0.1:{(string.IsNullOrEmpty(port) ? "8080" : port)}/readyz");
    }

    public static async Task<int> RunAsync(Uri url, HttpMessageHandler? handler = null, TextWriter? output = null)
    {
        output ??= Console.Out;
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(5);
        try
        {
            using var response = await http.GetAsync(url);
            await output.WriteLineAsync($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"unreachable: {ex.Message}");
            return 1;
        }
    }
}
