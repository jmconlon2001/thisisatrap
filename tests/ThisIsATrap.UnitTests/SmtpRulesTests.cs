using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ThisIsATrap.Tests;

public sealed class SmtpRulesTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SmtpRules _rules;

    public SmtpRulesTests() => _rules = new SmtpRules(TestStore.Create(_dir), NullLogger<SmtpRules>.Instance);

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("*@reject.test", "anyone@reject.test", true)]
    [InlineData("*@reject.test", "ANYONE@REJECT.TEST", true)]
    [InlineData("*@reject.test", "anyone@reject.test.com", false)]
    [InlineData("*@reject.test", "anyone@notreject.test", false)]
    [InlineData("user?@x.test", "user1@x.test", true)]
    [InlineData("user?@x.test", "user12@x.test", false)]
    [InlineData("a.b@x.test", "aXb@x.test", false)]           // '.' is literal, not a regex wildcard
    [InlineData("(weird)+[chars]@x.test", "(weird)+[chars]@x.test", true)]
    public void Glob_matching(string glob, string address, bool expected) => Assert.Equal(expected, SmtpRules.ToRegex(glob).IsMatch(address));

    [Fact]
    public void Defaults_apply_until_rules_are_saved()
    {
        Assert.Equal(SmtpRules.Defaults.Count, _rules.Get().Count);
        Assert.Equal(550, _rules.Find("rcpt", "x@reject.test")!.Rule.Code);
        Assert.Equal(452, _rules.Find("rcpt", "x@full.test")!.Rule.Code);
        Assert.Equal(451, _rules.Find("rcpt", "x@tempfail.test")!.Rule.Code);
        Assert.Equal(10_000, _rules.Find("rcpt", "x@slow.test")!.Rule.DelayMs);
        Assert.Null(_rules.Find("from", "x@blocked-sender.test"));   // shipped disabled
        Assert.Null(_rules.Find("rcpt", "user@example.com"));
    }

    [Fact]
    public void Stage_must_match_and_first_rule_wins()
    {
        _rules.Save([
            new SmtpRule { Match = "from", Pattern = "*@x.test", Code = 553 },
            new SmtpRule { Pattern = "first@x.test", Code = 550 },
            new SmtpRule { Pattern = "*@x.test", Code = 452, EnhancedCode = "4.2.2" },
        ]);

        Assert.Equal(553, _rules.Find("from", "a@x.test")!.Rule.Code);
        Assert.Equal(550, _rules.Find("rcpt", "first@x.test")!.Rule.Code);
        Assert.Equal(452, _rules.Find("rcpt", "other@x.test")!.Rule.Code);
    }

    [Fact]
    public void Saved_rules_persist_and_replace_defaults()
    {
        _rules.Save([new SmtpRule { Pattern = "only@x.test" }]);
        var reloaded = new SmtpRules(TestStore.Create(_dir), NullLogger<SmtpRules>.Instance);

        Assert.Single(reloaded.Get());
        Assert.Null(reloaded.Find("rcpt", "x@reject.test"));
        Assert.NotNull(reloaded.Find("rcpt", "only@x.test"));
    }

    [Fact]
    public void Saving_an_empty_list_disables_everything()
    {
        _rules.Save([]);
        Assert.Empty(_rules.Get());
        Assert.Null(_rules.Find("rcpt", "x@reject.test"));
    }

    [Fact]
    public void Validation_clamps_and_sanitises()
    {
        var r = SmtpRules.Validate(new SmtpRule
        {
            Match = "SOMETHING", Pattern = "  a@b.test  ", Code = 999, EnhancedCode = "nope",
            Message = "line one\r\n250 OK injected", DelayMs = 10_000_000,
        });

        Assert.Equal("rcpt", r.Match);
        Assert.Equal("a@b.test", r.Pattern);
        Assert.Equal(599, r.Code);
        Assert.Equal("5.0.0", r.EnhancedCode);
        Assert.DoesNotContain('\n', r.Message);
        Assert.DoesNotContain('\r', r.Message);
        Assert.Equal(SmtpRules.MaxDelayMs, r.DelayMs);
        Assert.Equal(450, SmtpRules.Validate(new SmtpRule { Code = 354 }).Code);   // 3xx isn't a valid MAIL/RCPT answer
        Assert.Equal("Simulated response", SmtpRules.Validate(new SmtpRule { Message = "  " }).Message);
        Assert.Equal("from", SmtpRules.Validate(new SmtpRule { Match = "FROM" }).Match);
    }

    [Fact]
    public void Rule_count_is_capped()
    {
        var saved = _rules.Save(Enumerable.Range(0, 80).Select(i => new SmtpRule { Pattern = $"u{i}@x.test" }));
        Assert.Equal(SmtpRules.MaxRules, saved.Count);
    }

    [Fact]
    public void Pathological_patterns_stay_fast()
    {
        _rules.Save([new SmtpRule { Pattern = string.Concat(Enumerable.Repeat("*a", 40)) + "!" }]);
        var sw = Stopwatch.StartNew();
        Assert.Null(_rules.Find("rcpt", new string('a', 5000)));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
