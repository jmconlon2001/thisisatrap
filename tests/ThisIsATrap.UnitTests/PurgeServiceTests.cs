using ThisIsATrap;
using Microsoft.Extensions.Logging.Abstractions;

namespace ThisIsATrap.Tests;

public sealed class PurgeServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task RunOnce_applies_rules_even_when_auto_purge_is_disabled_and_publishes_event()
    {
        var store = TestStore.Create(_dir, _time);
        store.SavePurgeSettings(new PurgeSettings { Enabled = false, MaxAgeHours = 1, MaxMessages = 0 });
        await store.AddAsync(TestMail.Build(), _time.Now.AddHours(-2));
        var hub = new EventHub();
        var (_, reader) = hub.Subscribe();

        var removed = new PurgeService(store, hub, NullLogger<PurgeService>.Instance).RunOnce();

        Assert.Equal(1, removed);
        Assert.Equal(1, PurgeService.LastRemoved);
        Assert.NotNull(PurgeService.LastRunUtc);
        Assert.True(reader.TryRead(out var evt));
        Assert.Contains("\"purged\"", evt);
    }

    [Fact]
    public async Task Background_loop_purges_when_enabled_and_stops_cleanly()
    {
        var store = TestStore.Create(_dir, _time);
        store.SavePurgeSettings(new PurgeSettings { Enabled = true, MaxAgeHours = 1, MaxMessages = 0, IntervalMinutes = 60 });
        await store.AddAsync(TestMail.Build(), _time.Now.AddHours(-2));
        var service = new PurgeService(store, new EventHub(), NullLogger<PurgeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await WaitUntil(() => store.Stats().Total == 0);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.Stats().Total);
    }

    [Fact]
    public async Task Wake_triggers_another_pass_before_the_interval()
    {
        var store = TestStore.Create(_dir, _time);
        store.SavePurgeSettings(new PurgeSettings { Enabled = true, MaxAgeHours = 1, MaxMessages = 0, IntervalMinutes = 60 });
        var service = new PurgeService(store, new EventHub(), NullLogger<PurgeService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await store.AddAsync(TestMail.Build(), _time.Now.AddHours(-2));
        service.Wake();
        await WaitUntil(() => store.Stats().Total == 0);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.Stats().Total);
    }

    [Fact]
    public async Task Disabled_auto_purge_leaves_mail_alone()
    {
        var store = TestStore.Create(_dir, _time);
        store.SavePurgeSettings(new PurgeSettings { Enabled = false, MaxAgeHours = 1, MaxMessages = 0 });
        await store.AddAsync(TestMail.Build(), _time.Now.AddHours(-2));
        var service = new PurgeService(store, new EventHub(), NullLogger<PurgeService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, store.Stats().Total);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
