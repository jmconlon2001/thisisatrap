using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ThisIsATrap.Tests;

public sealed class InboxSlugTests
{
    [Theory]
    [InlineData("billing-api", "billing-api")]
    [InlineData("Billing API", "billing-api")]
    [InlineData("  Orders  Service ", "orders-service")]
    [InlineData("ci@example.com", "ci-example.com")]
    [InlineData("Ünïcödé Ñame", "unicode-name")]
    [InlineData("a/b\\c:d", "a-b-c-d")]
    [InlineData("--trim--me__", "trim-me")]
    [InlineData("team.v2_beta", "team.v2_beta")]
    [InlineData("UPPER", "upper")]
    public void Normalizes_to_url_safe_keys(string input, string expected) => Assert.Equal(expected, InboxSlug.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("👋🎉")]
    public void Nothing_usable_gives_null(string? input) => Assert.Null(InboxSlug.Normalize(input));

    [Fact]
    public void Long_names_are_cut_to_64_characters()
    {
        var slug = InboxSlug.Normalize(new string('x', 200) + " tail");
        Assert.Equal(InboxSlug.MaxLength, slug!.Length);
    }
}

public sealed class InboxStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly MailStore _store;

    public InboxStoreTests() => _store = TestStore.Create(_dir, _time);

    public void Dispose() => _dir.Dispose();

    private async Task<MessageRecord> AddTo(Inbox inbox, string subject = "m", DateTimeOffset? at = null)
    {
        var r = await _store.AddAsync(TestMail.Build(subject: subject), at ?? _time.Now);
        // TestStore.AddAsync always targets Default; re-add into the requested inbox.
        _store.Delete(r.Id);
        using var raw = new MemoryStream();
        await TestMail.Build(subject: subject).WriteToAsync(raw);
        raw.Position = 0;
        var record = r with { Id = Guid.NewGuid().ToString("N"), InboxId = inbox.Id };
        await _store.AddAsync(record, raw, CancellationToken.None);
        return record;
    }

    [Fact]
    public void Default_inbox_always_exists_first()
    {
        var all = _store.ListInboxes();
        var d = Assert.Single(all);
        Assert.Equal(Inbox.DefaultSlug, d.Slug);
        Assert.True(d.IsDefault);
        Assert.Null(d.Purge);
    }

    [Fact]
    public void Create_returns_existing_for_same_slug()
    {
        var (a, createdA) = _store.CreateInbox("Billing API");
        var (b, createdB) = _store.CreateInbox("billing api");

        Assert.True(createdA);
        Assert.False(createdB);
        Assert.Equal(a.Id, b.Id);
        Assert.Equal("Billing API", b.Name);
        Assert.Equal("billing-api", b.Slug);
    }

    [Fact]
    public void Create_rejects_names_without_letters_or_digits() =>
        Assert.Throws<ArgumentException>(() => _store.CreateInbox("!!!"));

    [Fact]
    public void Resolve_routes_by_key_and_respects_auto_create()
    {
        Assert.True(_store.ResolveInbox(null, create: true).Inbox.IsDefault);
        Assert.True(_store.ResolveInbox("", create: true).Inbox.IsDefault);
        Assert.True(_store.ResolveInbox("new-app", create: false).Inbox.IsDefault);

        var (created, isNew) = _store.ResolveInbox("New App", create: true);
        Assert.True(isNew);
        Assert.Equal("new-app", created.Slug);
        Assert.Equal("New App", created.Name);

        var (again, isNewAgain) = _store.ResolveInbox("NEW-APP", create: false);
        Assert.False(isNewAgain);
        Assert.Equal(created.Id, again.Id);
    }

    [Fact]
    public async Task Listing_stats_read_and_delete_are_scoped_to_an_inbox()
    {
        var billing = _store.CreateInbox("billing").Inbox;
        var def = _store.GetInbox(Inbox.DefaultId)!;
        await AddTo(billing, "b1");
        await AddTo(billing, "b2");
        await AddTo(def, "d1");

        Assert.Equal(["b2", "b1"], _store.List(null, false, false, 0, 50, billing.Id).Items.Select(m => m.Subject).OrderDescending());
        Assert.All(_store.List(null, false, false, 0, 50, billing.Id).Items, m => Assert.Equal("billing", m.Inbox));
        Assert.Equal(3, _store.List(null, false, false, 0, 50).Total);
        Assert.Equal(2, _store.Stats(billing.Id).Total);

        _store.MarkAllRead(billing.Id);
        Assert.Equal(0, _store.Stats(billing.Id).Unread);
        Assert.Equal(1, _store.Stats(def.Id).Unread);

        Assert.Equal(2, _store.DeleteAll(billing.Id));
        Assert.Equal(1, _store.Stats().Total);
    }

    [Fact]
    public async Task Inbox_summaries_include_counts()
    {
        var a = _store.CreateInbox("alpha").Inbox;
        await AddTo(a);
        var summary = _store.ListInboxes().Single(i => i.Slug == "alpha");
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Unread);
        Assert.True(summary.TotalBytes > 0);
        Assert.Equal(Inbox.DefaultSlug, _store.ListInboxes()[0].Slug);
    }

    [Fact]
    public async Task Purge_count_limit_applies_per_inbox()
    {
        var a = _store.CreateInbox("a").Inbox;
        var b = _store.CreateInbox("b").Inbox;
        for (var i = 0; i < 3; i++) { await AddTo(a, $"a{i}", _time.Now.AddMinutes(i)); await AddTo(b, $"b{i}", _time.Now.AddMinutes(i)); }

        Assert.Equal(2, _store.Purge(new PurgeSettings { MaxAgeHours = 0, MaxMessages = 1 }, a.Id));
        Assert.Equal(1, _store.Stats(a.Id).Total);
        Assert.Equal(3, _store.Stats(b.Id).Total);
    }

    [Fact]
    public void Update_renames_and_sets_or_clears_custom_purge()
    {
        var inbox = _store.CreateInbox("ops").Inbox;
        var rules = new PurgeSettings { Enabled = true, MaxAgeHours = 1, MaxMessages = 5, IntervalMinutes = 15 };

        var updated = _store.UpdateInbox(inbox.Id, " Ops Team ", rules)!;
        Assert.Equal("Ops Team", updated.Name);
        Assert.Equal("ops", updated.Slug); // slug is stable so SMTP usernames keep working
        Assert.Equal(rules, updated.Purge);

        Assert.Null(_store.UpdateInbox(inbox.Id, "Ops", null)!.Purge);
        Assert.Null(_store.UpdateInbox(999, "nope", null));
        Assert.Throws<ArgumentException>(() => _store.UpdateInbox(inbox.Id, "  ", null));
    }

    [Fact]
    public async Task Delete_removes_inbox_and_its_mail_but_never_default()
    {
        var inbox = _store.CreateInbox("temp").Inbox;
        var m = await AddTo(inbox);

        Assert.Equal(1, _store.DeleteInbox(inbox.Id));
        Assert.Null(_store.GetInbox("temp"));
        Assert.False(File.Exists(_store.RawPath(m.Id)));
        Assert.Null(_store.DeleteInbox(inbox.Id));
        Assert.Throws<InvalidOperationException>(() => _store.DeleteInbox(Inbox.DefaultId));
    }

    [Fact]
    public async Task Get_message_reports_its_inbox()
    {
        var inbox = _store.CreateInbox("x").Inbox;
        var m = await AddTo(inbox);
        Assert.Equal(inbox.Id, _store.Get(m.Id)!.InboxId);
    }
}

public sealed class InboxMigrationTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Database_from_before_inboxes_is_migrated_into_default()
    {
        // The schema as it was before inboxes existed.
        using (var db = new SqliteConnection($"Data Source={Path.Combine(_dir.Path, MailStore.DatabaseFile)}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE messages (id TEXT PRIMARY KEY, received INTEGER NOT NULL, from_addr TEXT NOT NULL, to_addrs TEXT NOT NULL,
                    cc_addrs TEXT NOT NULL, subject TEXT NOT NULL, preview TEXT NOT NULL, size INTEGER NOT NULL, attachment_count INTEGER NOT NULL,
                    is_read INTEGER NOT NULL DEFAULT 0, envelope_from TEXT NOT NULL, envelope_to TEXT NOT NULL, client_ip TEXT NOT NULL);
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO messages VALUES ('0123456789abcdef0123456789abcdef', 1, 'a@x', 'b@x', '', 'old mail', '', 10, 0, 0, 'a@x', 'b@x', '127.0.0.1');
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = TestStore.Create(_dir);
        var again = TestStore.Create(_dir); // running the migration twice is harmless

        var item = Assert.Single(again.List(null, false, false, 0, 10, Inbox.DefaultId).Items);
        Assert.Equal("old mail", item.Subject);
        Assert.Equal(Inbox.DefaultSlug, item.Inbox);
        Assert.Equal(1, store.ListInboxes().Single().Total);
    }
}

public sealed class InboxPurgeServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeTime _time = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Inbox_rules_override_global_and_run_now_forces_all()
    {
        var store = TestStore.Create(_dir, _time);
        store.SavePurgeSettings(new PurgeSettings { Enabled = false, MaxAgeHours = 1, MaxMessages = 0 });
        var strict = store.CreateInbox("strict").Inbox;
        store.UpdateInbox(strict.Id, "strict", new PurgeSettings { Enabled = true, MaxAgeHours = 1, MaxMessages = 0 });

        // One old message in Default (global rules: disabled) and one in "strict" (own rules: enabled).
        var old = _time.Now.AddHours(-2);
        await store.AddAsync(TestMail.Build(subject: "default-old"), old);
        using (var raw = new MemoryStream())
        {
            await TestMail.Build(subject: "strict-old").WriteToAsync(raw, TestContext.Current.CancellationToken);
            raw.Position = 0;
            await store.AddAsync(new MessageRecord(Guid.NewGuid().ToString("N"), old, "a", "b", "", "strict-old", "", raw.Length, 0, false, "", "", "", strict.Id), raw, CancellationToken.None);
        }

        var service = new PurgeService(store, new EventHub(), NullLogger<PurgeService>.Instance);
        await service.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && store.Stats(strict.Id).Total > 0; i++) await Task.Delay(25, TestContext.Current.CancellationToken);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.Stats(strict.Id).Total);
        Assert.Equal(1, store.Stats(Inbox.DefaultId).Total);

        Assert.Equal(1, service.RunOnce());
        Assert.Equal(0, store.Stats().Total);
    }
}
