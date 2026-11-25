using MailTrap;

[assembly: Trait("Category", "Unit")]

namespace MailTrap.Tests;

public sealed class MailStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly MailStore _store;

    public MailStoreTests() => _store = TestStore.Create(_dir, _time);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Add_then_get_round_trips_every_field()
    {
        var msg = TestMail.Build(subject: "Round trip", cc: "cc@example.com", attachments: [TestMail.Csv()]);
        var added = await _store.AddAsync(msg, _time.Now, envelopeTo: "user@example.com, hidden@example.com", clientIp: "10.0.0.5");

        var got = _store.Get(added.Id);

        Assert.NotNull(got);
        Assert.Equal(added with { IsRead = false }, got);
        Assert.True(File.Exists(_store.RawPath(added.Id)));
        Assert.Equal(1, got!.AttachmentCount);
    }

    [Fact]
    public void Get_unknown_id_returns_null() => Assert.Null(_store.Get(Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task List_is_newest_first_and_pages()
    {
        for (var i = 0; i < 5; i++)
            await _store.AddAsync(TestMail.Build(subject: $"m{i}"), _time.Now.AddMinutes(i));

        var (page1, total) = _store.List(null, false, false, 0, 2);
        var (page3, _) = _store.List(null, false, false, 4, 2);

        Assert.Equal(5, total);
        Assert.Equal(["m4", "m3"], page1.Select(m => m.Subject));
        Assert.Equal(["m0"], page3.Select(m => m.Subject));
    }

    [Theory]
    [InlineData(0, 1)]        // take is clamped to at least 1
    [InlineData(10_000, 3)]   // and at most 500
    public async Task List_clamps_take(int take, int expected)
    {
        for (var i = 0; i < 3; i++) await _store.AddAsync(TestMail.Build());
        Assert.Equal(expected, _store.List(null, false, false, 0, take).Items.Count);
    }

    [Fact]
    public async Task List_negative_skip_is_treated_as_zero()
    {
        await _store.AddAsync(TestMail.Build());
        Assert.Single(_store.List(null, false, false, -5, 10).Items);
    }

    [Theory]
    [InlineData("invoice", 1)]
    [InlineData("INVOICE", 1)]          // case-insensitive
    [InlineData("billing@", 1)]         // matches from
    [InlineData("ops@example", 1)]      // matches cc
    [InlineData("hidden@", 1)]          // matches envelope (bcc)
    [InlineData("nothing-like-this", 0)]
    public async Task Search_matches_subject_addresses_and_envelope(string q, int expected)
    {
        await _store.AddAsync(TestMail.Build(subject: "Your invoice", from: "billing@shop.test", cc: "ops@example.com"),
            envelopeTo: "user@example.com, ops@example.com, hidden@example.com");
        await _store.AddAsync(TestMail.Build(subject: "Other"));

        Assert.Equal(expected, _store.List(q, false, false, 0, 50).Total);
    }

    [Theory]
    [InlineData("100%")]
    [InlineData("a_b")]
    [InlineData(@"c:\temp")]
    public async Task Search_treats_like_wildcards_literally(string literal)
    {
        await _store.AddAsync(TestMail.Build(subject: $"Contains {literal} here"));
        await _store.AddAsync(TestMail.Build(subject: "100 percent aXb c:/temp"));

        var (items, total) = _store.List(literal, false, false, 0, 50);

        Assert.Equal(1, total);
        Assert.Contains(literal, items[0].Subject);
    }

    [Fact]
    public async Task Unread_and_attachment_filters()
    {
        var a = await _store.AddAsync(TestMail.Build(subject: "read, no att"));
        await _store.AddAsync(TestMail.Build(subject: "unread, att", attachments: [TestMail.Pdf()]));
        _store.SetRead(a.Id, true);

        Assert.Equal(["unread, att"], _store.List(null, true, false, 0, 50).Items.Select(m => m.Subject));
        Assert.Equal(["unread, att"], _store.List(null, false, true, 0, 50).Items.Select(m => m.Subject));
    }

    [Fact]
    public async Task SetRead_MarkAllRead_and_stats()
    {
        var a = await _store.AddAsync(TestMail.Build());
        await _store.AddAsync(TestMail.Build());

        Assert.Equal(2, _store.Stats().Unread);
        _store.SetRead(a.Id, true);
        Assert.Equal(1, _store.Stats().Unread);
        _store.SetRead(a.Id, false);
        Assert.Equal(2, _store.Stats().Unread);
        _store.MarkAllRead();

        var stats = _store.Stats();
        Assert.Equal(0, stats.Unread);
        Assert.Equal(2, stats.Total);
        Assert.True(stats.TotalBytes > 0);
    }

    [Fact]
    public void Stats_on_empty_store_are_zero() => Assert.Equal(new MailStats(0, 0, 0), _store.Stats());

    [Fact]
    public async Task Delete_removes_row_and_file()
    {
        var a = await _store.AddAsync(TestMail.Build());
        var path = _store.RawPath(a.Id);

        Assert.True(_store.Delete(a.Id));
        Assert.False(_store.Delete(a.Id));
        Assert.Null(_store.Get(a.Id));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteAll_removes_everything()
    {
        for (var i = 0; i < 3; i++) await _store.AddAsync(TestMail.Build());

        Assert.Equal(3, _store.DeleteAll());
        Assert.Equal(0, _store.Stats().Total);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir.Path, "messages")));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..\\..\\windows")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdeg")] // 32 chars, not hex
    [InlineData("0123456789abcdef0123456789abcdef0")] // 33 chars
    public void RawPath_rejects_anything_but_32_hex(string id) =>
        Assert.Throws<ArgumentException>(() => _store.RawPath(id));

    [Fact]
    public async Task Purge_by_age_removes_only_old_messages()
    {
        await _store.AddAsync(TestMail.Build(subject: "old"), _time.Now.AddHours(-73));
        await _store.AddAsync(TestMail.Build(subject: "new"), _time.Now.AddHours(-1));

        var removed = _store.Purge(new PurgeSettings { MaxAgeHours = 72, MaxMessages = 0 });

        Assert.Equal(1, removed);
        Assert.Equal(["new"], _store.List(null, false, false, 0, 50).Items.Select(m => m.Subject));
    }

    [Fact]
    public async Task Purge_by_count_keeps_newest()
    {
        for (var i = 0; i < 5; i++) await _store.AddAsync(TestMail.Build(subject: $"m{i}"), _time.Now.AddMinutes(i));

        Assert.Equal(3, _store.Purge(new PurgeSettings { MaxAgeHours = 0, MaxMessages = 2 }));
        Assert.Equal(["m4", "m3"], _store.List(null, false, false, 0, 50).Items.Select(m => m.Subject));
    }

    [Fact]
    public async Task Purge_combines_rules_without_double_counting()
    {
        await _store.AddAsync(TestMail.Build(subject: "old1"), _time.Now.AddHours(-100));
        await _store.AddAsync(TestMail.Build(subject: "old2"), _time.Now.AddHours(-99));
        await _store.AddAsync(TestMail.Build(subject: "new1"), _time.Now.AddHours(-2));
        await _store.AddAsync(TestMail.Build(subject: "new2"), _time.Now.AddHours(-1));

        // Both old messages violate both rules; they must be counted once each.
        Assert.Equal(3, _store.Purge(new PurgeSettings { MaxAgeHours = 72, MaxMessages = 1 }));
        Assert.Equal(["new2"], _store.List(null, false, false, 0, 50).Items.Select(m => m.Subject));
    }

    [Fact]
    public async Task Purge_with_zero_limits_removes_nothing()
    {
        await _store.AddAsync(TestMail.Build(), _time.Now.AddYears(-1));
        Assert.Equal(0, _store.Purge(new PurgeSettings { MaxAgeHours = 0, MaxMessages = 0 }));
    }

    [Fact]
    public async Task Purge_age_follows_the_clock()
    {
        await _store.AddAsync(TestMail.Build(), _time.Now);
        var rules = new PurgeSettings { MaxAgeHours = 1, MaxMessages = 0 };

        Assert.Equal(0, _store.Purge(rules));
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, _store.Purge(rules));
    }

    [Fact]
    public void Purge_settings_default_then_round_trip()
    {
        Assert.Equal(new PurgeSettings(), _store.GetPurgeSettings());

        var custom = new PurgeSettings { Enabled = false, MaxAgeHours = 5, MaxMessages = 6, IntervalMinutes = 7 };
        _store.SavePurgeSettings(custom);
        Assert.Equal(custom, _store.GetPurgeSettings());

        // Survives a new store instance on the same folder.
        Assert.Equal(custom, TestStore.Create(_dir, _time).GetPurgeSettings());
    }

    [Fact]
    public void Corrupt_purge_settings_fall_back_to_defaults()
    {
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir.Path, "mailtrap.db")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO settings (key, value) VALUES ('purge', '{not json')";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(new PurgeSettings(), _store.GetPurgeSettings());
    }

    [Fact]
    public void Relative_data_path_resolves_against_content_root()
    {
        var store = new MailStore(
            Microsoft.Extensions.Options.Options.Create(new MailTrapOptions { DataPath = "rel-data" }),
            new TestEnv(_dir.Path), Microsoft.Extensions.Logging.Abstractions.NullLogger<MailStore>.Instance);

        Assert.Equal(Path.Combine(_dir.Path, "rel-data"), store.DataPath);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "rel-data", "mailtrap.db")));
    }
}
