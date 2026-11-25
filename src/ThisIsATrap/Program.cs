using ThisIsATrap;
using ThisIsATrap.Endpoints;
using ThisIsATrap.Health;

// Docker HEALTHCHECK: probe the running instance instead of starting a new one.
if (HealthProbe.IsRequested(args))
    return await HealthProbe.RunAsync(HealthProbe.ReadyUrl(Environment.GetEnvironmentVariable));

var builder = WebApplication.CreateBuilder(args);

// Bind the legacy section first so the new one wins wherever both set a value.
builder.Services.Configure<TrapOptions>(builder.Configuration.GetSection(TrapOptions.LegacySection));
builder.Services.Configure<TrapOptions>(builder.Configuration.GetSection(TrapOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<MailStore>();
builder.Services.AddSingleton<SmtpTls>();
builder.Services.AddSingleton<SmtpRules>();
builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection($"{TrapOptions.Section}:Webhooks"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebhookOptions>>().Value);
// Webhook receivers are usually internal, so private networks are allowed; cloud metadata addresses never are.
builder.Services.AddHttpClient(WebhookService.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => ThisIsATrap.Analysis.OutboundPolicy.CreateHandler(allowPrivate: true));
builder.Services.AddSingleton<WebhookService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookService>());
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<PurgeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PurgeService>());
builder.Services.AddHostedService<SmtpListenerService>();
builder.Services.AddTrapHealth();
builder.Services.AddTrapAnalysis(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Always revalidate (cheap 304 via ETag) so an upgrade never leaves browsers running stale JS/CSS.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapTrapHealth();

var api = app.MapGroup("/api");
api.MapMessages();
api.MapInboxes();
api.MapSettings();
api.MapAnalysis();

app.Run();
return 0;

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program;
