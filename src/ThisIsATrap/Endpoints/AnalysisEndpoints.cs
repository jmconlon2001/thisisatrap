using Microsoft.Extensions.Caching.Memory;
using ThisIsATrap.Analysis;

namespace ThisIsATrap.Endpoints;

/// <summary>Per-message analysis: HTML client support, spam score, links. Results are cached briefly per message.</summary>
public static class AnalysisEndpoints
{
    private static readonly MemoryCacheEntryOptions CacheFor = new() { SlidingExpiration = TimeSpan.FromMinutes(10), Size = 1 };

    public static IServiceCollection AddTrapAnalysis(this IServiceCollection services)
    {
        services.AddMemoryCache(o => o.SizeLimit = 500);
        services.AddSingleton(SupportData.Default);
        services.AddSingleton<HtmlCheck>();
        return services;
    }

    public static void MapAnalysis(this RouteGroupBuilder api)
    {
        api.MapGet("/messages/{id}/html-check", async (string id, MailStore store, HtmlCheck check, IMemoryCache cache, CancellationToken ct) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            var report = await cache.GetOrCreateAsync("html-check:" + id, async entry =>
            {
                entry.SetOptions(CacheFor);
                var message = await MessageParser.LoadAsync(store.RawPath(id), ct);
                return check.Run(message.HtmlBody);
            });
            return Results.Ok(report);
        });
    }
}
