using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ThisIsATrap.Health;

namespace ThisIsATrap.Tests;

public sealed class HealthTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    private static Task<HealthCheckResult> Run(IHealthCheck check) => check.CheckHealthAsync(new HealthCheckContext());

    // ---------- RuntimeStats ----------

    [Fact]
    public void Runtime_stats_count_messages_and_track_uptime()
    {
        var stats = new RuntimeStats(_time);
        Assert.Null(stats.LastMessageUtc);
        Assert.Equal("starting", stats.SmtpStatus);
        Assert.False(stats.SmtpListening);

        _time.Advance(TimeSpan.FromMinutes(5));
        stats.MessageReceived();
        stats.MessageReceived();
        stats.MessageRejected();

        Assert.Equal(2, stats.MessagesReceived);
        Assert.Equal(1, stats.MessagesRejected);
        Assert.Equal(_time.Now, stats.LastMessageUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), stats.Uptime);
    }

    [Fact]
    public async Task Runtime_stats_are_thread_safe()
    {
        var stats = new RuntimeStats(_time);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => { for (var i = 0; i < 1000; i++) stats.MessageReceived(); })));
        Assert.Equal(8000, stats.MessagesReceived);
    }

    // ---------- SMTP check ----------

    [Theory]
    [InlineData("listening on port 25", HealthStatus.Healthy)]
    [InlineData("starting", HealthStatus.Unhealthy)]
    [InlineData("error: address already in use", HealthStatus.Unhealthy)]
    public async Task Smtp_check_follows_listener_status(string status, HealthStatus expected)
    {
        var stats = new RuntimeStats(_time) { SmtpStatus = status };
        var result = await Run(new SmtpHealthCheck(stats));

        Assert.Equal(expected, result.Status);
        Assert.Contains(status, result.Description);
        Assert.Equal(status, result.Data["status"]);
    }

    // ---------- Database check ----------

    [Fact]
    public async Task Database_check_is_healthy_and_reports_count()
    {
        var store = TestStore.Create(_dir, _time);
        await store.AddAsync(TestMail.Build());

        var result = await Run(new DatabaseHealthCheck(store));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(1, result.Data["messages"]);
    }

    [Fact]
    public async Task Database_check_is_unhealthy_when_the_folder_is_gone()
    {
        var store = TestStore.Create(_dir, _time);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir.Path, recursive: true);

        var result = await Run(new DatabaseHealthCheck(store));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    // ---------- Storage check ----------

    [Fact]
    public async Task Storage_check_is_healthy_with_space_and_leaves_no_probe_files()
    {
        var store = TestStore.Create(_dir, _time);

        var result = await Run(new StorageHealthCheck(store, _ => (10L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(10L * 1024 * 1024 * 1024, result.Data["freeBytes"]);
        Assert.Empty(Directory.GetFiles(_dir.Path, ".health-*"));
    }

    [Fact]
    public async Task Storage_check_is_degraded_when_disk_is_nearly_full()
    {
        var store = TestStore.Create(_dir, _time);
        var result = await Run(new StorageHealthCheck(store, _ => (StorageHealthCheck.LowSpaceBytes - 1, 100L * 1024 * 1024 * 1024)));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Low disk space", result.Description);
    }

    [Fact]
    public async Task Storage_check_is_healthy_when_disk_size_is_unknown()
    {
        var store = TestStore.Create(_dir, _time);
        var result = await Run(new StorageHealthCheck(store, _ => null));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.False(result.Data.ContainsKey("freeBytes"));
    }

    [Fact]
    public async Task Storage_check_is_unhealthy_when_folder_is_not_writable()
    {
        var store = TestStore.Create(_dir, _time);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir.Path, recursive: true);

        var result = await Run(new StorageHealthCheck(store, _ => (1L << 40, 1L << 41)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not writable", result.Description);
    }

    [Fact]
    public void Disk_space_of_a_real_path_is_reported()
    {
        var space = StorageHealthCheck.DiskSpace(_dir.Path);
        Assert.NotNull(space);
        Assert.InRange(space!.Value.Free, 1, space.Value.Total);
    }

    [Fact]
    public async Task Database_bytes_include_the_index_file()
    {
        var store = TestStore.Create(_dir, _time);
        await store.AddAsync(TestMail.Build());
        Assert.True(store.DatabaseBytes() > 0);
    }

    // ---------- Docker probe ----------

    [Theory]
    [InlineData(new[] { "--health-check" }, true)]
    [InlineData(new[] { "--HEALTH-CHECK" }, true)]
    [InlineData(new[] { "--urls", "http://x" }, false)]
    [InlineData(new string[0], false)]
    public void Probe_is_requested_only_by_its_flag(string[] args, bool expected) =>
        Assert.Equal(expected, HealthProbe.IsRequested(args));

    [Theory]
    [InlineData("8080", null, "http://127.0.0.1:8080/readyz")]
    [InlineData("5000;6000", null, "http://127.0.0.1:5000/readyz")]
    [InlineData(null, "http://+:7000", "http://127.0.0.1:7000/readyz")]
    [InlineData(null, "http://*:7001;https://*:7443", "http://127.0.0.1:7001/readyz")]
    [InlineData(null, null, "http://127.0.0.1:8080/readyz")]
    public void Probe_url_follows_aspnetcore_port_settings(string? ports, string? urls, string expected)
    {
        var env = new Dictionary<string, string?> { ["ASPNETCORE_HTTP_PORTS"] = ports, ["ASPNETCORE_URLS"] = urls };
        Assert.Equal(expected, HealthProbe.ReadyUrl(k => env.GetValueOrDefault(k)).ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 0)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1)]
    public async Task Probe_exit_code_follows_readiness(HttpStatusCode code, int expected)
    {
        var output = new StringWriter();
        var exit = await HealthProbe.RunAsync(new Uri("http://127.0.0.1:1/readyz"), new StubHandler(code, "{\"status\":\"x\"}"), output);

        Assert.Equal(expected, exit);
        Assert.Contains(((int)code).ToString(), output.ToString());
    }

    [Fact]
    public async Task Probe_fails_when_app_is_unreachable()
    {
        var output = new StringWriter();
        var exit = await HealthProbe.RunAsync(new Uri("http://127.0.0.1:1/readyz"), new StubHandler(null, ""), output);

        Assert.Equal(1, exit);
        Assert.StartsWith("unreachable", output.ToString());
    }

    private sealed class StubHandler(HttpStatusCode? code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            code is null
                ? throw new HttpRequestException("connection refused")
                : Task.FromResult(new HttpResponseMessage(code.Value) { Content = new StringContent(body) });
    }
}
