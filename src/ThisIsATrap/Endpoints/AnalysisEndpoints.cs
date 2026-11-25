using Microsoft.Extensions.Caching.Memory;
using ThisIsATrap.Analysis;

namespace ThisIsATrap.Endpoints;

/// <summary>Per-message analysis: HTML client support, spam score, links. Results are cached briefly per message.</summary>
public static class AnalysisEndpoints
{
    private static readonly MemoryCacheEntryOptions CacheFor = new() { SlidingExpiration = TimeSpan.FromMinutes(10), Size = 1 };

    public static IServiceCollection AddTrapAnalysis(this IServiceCollection services, IConfiguration config)
    {
        services.AddMemoryCache(o => o.SizeLimit = 500);
        services.AddSingleton(SupportData.Default);
        services.AddSingleton<HtmlCheck>();

        // Bound lazily through IOptions (legacy section first, so the new one wins).
        services.Configure<SpamAssassinOptions>(config.GetSection($"{TrapOptions.LegacySection}:SpamAssassin"));
        services.Configure<SpamAssassinOptions>(config.GetSection($"{TrapOptions.Section}:SpamAssassin"));
        services.AddSingleton(sp => new SpamAssassinClient(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SpamAssassinOptions>>().Value));
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

        // Built-in content rules always; real SpamAssassin scoring too when ThisIsATrap:SpamAssassin:Host is set.
        api.MapGet("/messages/{id}/spam", async (string id, MailStore store, SpamAssassinClient spamd, IMemoryCache cache, CancellationToken ct) =>
        {
            var record = store.Get(id);
            if (record is null) return Results.NotFound();
            var report = await cache.GetOrCreateAsync("spam:" + id, async entry =>
            {
                entry.SetOptions(CacheFor);
                var path = store.RawPath(id);
                var message = await MessageParser.LoadAsync(path, ct);
                var rules = SpamCheck.Run(message, record.EnvelopeFrom, record.Size);
                var builtin = Math.Round(rules.Sum(r => r.Score), 1);

                SpamAssassinResult? sa = null;
                if (spamd.Enabled) sa = await spamd.CheckAsync(await File.ReadAllBytesAsync(path, ct), ct);
                if (sa is { Available: false }) entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30); // retry soon

                return sa is { Available: true }
                    ? new SpamReport(sa.Score, sa.Threshold, SpamCheck.Verdict(sa.Score * SpamCheck.Threshold / Math.Max(0.1, sa.Threshold)), "spamassassin", rules, sa)
                    : new SpamReport(builtin, SpamCheck.Threshold, SpamCheck.Verdict(builtin), "builtin", rules, sa);
            });
            return Results.Ok(report);
        });
    }
}
