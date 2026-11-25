using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThisIsATrap.Logs;

namespace ThisIsATrap.Tests;

public sealed class SmtpAccessTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SmtpAccess _access;

    public SmtpAccessTests() => _access = new SmtpAccess(TestStore.Create(_dir));

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5/32")]
    [InlineData(" 192.168.1.0/24 ", "192.168.1.0/24")]
    [InlineData("fd00::/8", "fd00::/8")]
    [InlineData("::1", "::1/128")]
    [InlineData("::ffff:10.1.2.3", "10.1.2.3/32")]
    public void Parses_addresses_and_ranges(string entry, string normalized)
    {
        Assert.True(SmtpAccess.TryParseEntry(entry, out var network));
        Assert.Equal(normalized, network.ToString());
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.1/24")]   // host bits set: ambiguous, rejected
    [InlineData("")]
    public void Rejects_invalid_entries(string entry) => Assert.False(SmtpAccess.TryParseEntry(entry, out _));

    [Fact]
    public void Disabled_allows_everyone()
    {
        Assert.True(_access.IsAllowed(IPAddress.Parse("203.0.113.9")));
        Assert.True(_access.IsAllowed(null));
    }

    [Fact]
    public void Enabled_allows_only_listed_networks_plus_loopback()
    {
        _access.Save(new SmtpAccessSettings { Enabled = true, Allow = ["192.168.1.0/24", "10.0.0.5"] });

        Assert.True(_access.IsAllowed(IPAddress.Parse("192.168.1.77")));
        Assert.True(_access.IsAllowed(IPAddress.Parse("::ffff:192.168.1.77")));   // IPv4-mapped form from dual-stack sockets
        Assert.True(_access.IsAllowed(IPAddress.Parse("10.0.0.5")));
        Assert.True(_access.IsAllowed(IPAddress.Loopback));
        Assert.True(_access.IsAllowed(IPAddress.IPv6Loopback));
        Assert.False(_access.IsAllowed(IPAddress.Parse("10.0.0.6")));
        Assert.False(_access.IsAllowed(IPAddress.Parse("192.168.2.1")));
        Assert.False(_access.IsAllowed(null));
    }

    [Fact]
    public void Loopback_can_be_excluded()
    {
        _access.Save(new SmtpAccessSettings { Enabled = true, Allow = ["10.0.0.0/8"], AlwaysAllowLoopback = false });
        Assert.False(_access.IsAllowed(IPAddress.Loopback));
    }

    [Fact]
    public void Save_normalises_dedupes_skips_comments_and_persists()
    {
        var saved = _access.Save(new SmtpAccessSettings { Enabled = true, Allow = ["10.0.0.5", " 10.0.0.5/32", "# office", "", "192.168.0.0/16"] });

        Assert.Equal(["10.0.0.5/32", "192.168.0.0/16"], saved.Allow);
        var reloaded = new SmtpAccess(TestStore.Create(_dir)).Get();
        Assert.True(reloaded.Enabled);
        Assert.Equal(saved.Allow, reloaded.Allow);
    }

    [Fact]
    public void Save_rejects_bad_entries_and_a_lockout()
    {
        var ex = Assert.Throws<ArgumentException>(() => _access.Save(new SmtpAccessSettings { Allow = ["10.0.0.0/8", "oops"] }));
        Assert.Contains("oops", ex.Message);
        Assert.Throws<ArgumentException>(() => _access.Save(new SmtpAccessSettings { Enabled = true, Allow = [], AlwaysAllowLoopback = false }));
        Assert.Throws<ArgumentException>(() => _access.Save(new SmtpAccessSettings { Allow = Enumerable.Range(0, 201).Select(i => $"10.0.{i / 250}.{i % 250}").ToList() }));
    }

    [Fact]
    public void Changes_take_effect_immediately()
    {
        _access.Save(new SmtpAccessSettings { Enabled = true, Allow = ["10.0.0.1"] });
        Assert.False(_access.IsAllowed(IPAddress.Parse("10.0.0.2")));
        _access.Save(new SmtpAccessSettings { Enabled = true, Allow = ["10.0.0.2"] });
        Assert.True(_access.IsAllowed(IPAddress.Parse("10.0.0.2")));
    }
}

public sealed class LogBufferTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Keeps_only_the_newest_entries_and_counts_drops()
    {
        var buffer = new LogBuffer(100);
        for (var i = 0; i < 150; i++) buffer.Add(T, LogLevel.Information, "Cat", $"m{i}", null);

        var page = buffer.Query(take: 5000);
        Assert.Equal(100, page.Buffered);
        Assert.Equal(50, page.Dropped);
        Assert.Equal("m50", page.Entries[0].Message);
        Assert.Equal(150, page.LastSeq);
    }

    [Fact]
    public void Filters_by_sequence_level_text_and_category()
    {
        var buffer = new LogBuffer();
        buffer.Add(T, LogLevel.Debug, "SmtpListenerService", "SMTP connection from 10.0.0.5", null);
        buffer.Add(T, LogLevel.Information, "SmtpListenerService", "Trapped message abc", null);
        buffer.Add(T, LogLevel.Warning, "WebhookService", "Webhook failed", null);
        buffer.Add(T, LogLevel.Error, "PurgeService", "Auto-purge failed", new InvalidOperationException("disk full"));

        Assert.Equal(4, buffer.Query().Entries.Count);
        Assert.Equal(["Webhook failed", "Auto-purge failed"], buffer.Query(minLevel: "Warning").Entries.Select(e => e.Message));
        Assert.Equal(["Trapped message abc", "Webhook failed", "Auto-purge failed"], buffer.Query(after: 1).Entries.Select(e => e.Message));
        Assert.Single(buffer.Query(search: "10.0.0.5").Entries);
        Assert.Single(buffer.Query(search: "disk full").Entries);              // matches the exception text
        Assert.Equal(2, buffer.Query(category: "smtp").Entries.Count);
        Assert.Equal(["Auto-purge failed"], buffer.Query(take: 1).Entries.Select(e => e.Message));   // newest wins
        Assert.Contains("disk full", buffer.Query(minLevel: "Error").Entries.Single().Exception);
    }

    [Fact]
    public void Clear_empties_but_keeps_sequence()
    {
        var buffer = new LogBuffer();
        buffer.Add(T, LogLevel.Information, "C", "a", null);
        Assert.Equal(1, buffer.Clear());
        buffer.Add(T, LogLevel.Information, "C", "b", null);
        var page = buffer.Query();
        Assert.Equal(2, page.LastSeq);
        Assert.Equal("b", Assert.Single(page.Entries).Message);
    }

    [Fact]
    public void Capacity_is_clamped() => Assert.Equal(100, new LogBuffer(5).Capacity);

    [Fact]
    public void Provider_feeds_the_buffer_with_short_categories()
    {
        var buffer = new LogBuffer();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new BufferLoggerProvider(buffer)).SetMinimumLevel(LogLevel.Debug));

        factory.CreateLogger("ThisIsATrap.SmtpListenerService").LogInformation("Hello {Name}", "world");
        factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogWarning("Careful");

        var entries = buffer.Query().Entries;
        Assert.Equal("SmtpListenerService", entries[0].Category);
        Assert.Equal("Hello world", entries[0].Message);
        Assert.Equal("Microsoft.Diagnostics", entries[1].Category);
        Assert.Equal("Warning", entries[1].Level);
    }

    [Theory]
    [InlineData("ThisIsATrap.Health.RuntimeStats", "RuntimeStats")]
    [InlineData("Program", "Program")]
    [InlineData("System.Net.Http", "System.Net.Http")]
    [InlineData("Microsoft.Hosting.Lifetime", "Microsoft.Hosting.Lifetime")]
    [InlineData("Microsoft.AspNetCore.Routing.EndpointMiddleware", "Microsoft.EndpointMiddleware")]
    public void Shortens_categories(string category, string expected) => Assert.Equal(expected, BufferLoggerProvider.Shorten(category));
}

public sealed class EnvironmentLabelTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private EnvironmentLabels Create(string? label = null, string? color = null) =>
        new(TestStore.Create(_dir), Options.Create(new TrapOptions { EnvironmentLabel = label, EnvironmentColor = color }));

    [Fact]
    public void No_label_by_default()
    {
        var badge = Create().Get();
        Assert.Null(badge.Label);
        Assert.Equal("none", badge.Source);
    }

    [Theory]
    [InlineData("Production", null, "red")]
    [InlineData("live", null, "red")]
    [InlineData("Staging", null, "amber")]
    [InlineData("QA", null, "amber")]
    [InlineData("UAT", null, "amber")]
    [InlineData("Dev", null, "green")]
    [InlineData("Team Alpha", null, "indigo")]
    [InlineData("QA", "teal", "teal")]
    [InlineData("QA", "#ABC", "#abc")]
    [InlineData("QA", "chartreuse", "amber")]   // unknown colour in config → automatic
    public void Configured_label_and_colour(string label, string? color, string expected)
    {
        var badge = Create(label, color).Get();
        Assert.Equal(label, badge.Label);
        Assert.Equal(expected, badge.Color);
        Assert.Equal("config", badge.Source);
    }

    [Fact]
    public void Saved_label_overrides_config_until_cleared()
    {
        var labels = Create("QA");

        var saved = labels.Save(new EnvironmentBadgeInput("  Stage 2 ", "purple"));
        Assert.Equal(("Stage 2", "purple", "settings"), (saved.Label, saved.Color, saved.Source));

        var cleared = labels.Save(new EnvironmentBadgeInput("", null));
        Assert.Equal(("QA", "config"), (cleared.Label, cleared.Source));
    }

    [Fact]
    public void Invalid_colour_is_rejected_when_saved() =>
        Assert.Throws<ArgumentException>(() => Create().Save(new EnvironmentBadgeInput("QA", "url(javascript:alert(1))")));

    [Theory]
    [InlineData("  QA  ", "QA")]
    [InlineData("a\u0000b\nc", "abc")]
    [InlineData("This label is far too long to fit", "This label is far too lo")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Labels_are_cleaned(string? input, string? expected) => Assert.Equal(expected, EnvironmentLabels.Clean(input));
}
