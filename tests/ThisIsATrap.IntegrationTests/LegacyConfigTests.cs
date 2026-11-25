namespace ThisIsATrap.IntegrationTests;

/// <summary>An install configured before the rename (MailTrap section / MailTrap__ env vars).</summary>
public sealed class LegacyConfigApp : TrapApp
{
    protected override IDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["MailTrap:PublicSmtpPort"] = "2626",   // only set in the legacy section: must be honoured
        ["MailTrap:MaxMessageSizeMb"] = "9",    // set in both: the new section must win
    };
}

public sealed class LegacyConfigTests(LegacyConfigApp app) : IClassFixture<LegacyConfigApp>
{
    [Fact]
    public async Task Legacy_section_still_applies_but_new_section_wins()
    {
        var smtp = (await app.GetJsonAsync("/api/settings")).GetProperty("smtp");

        Assert.Equal(2626, smtp.GetProperty("port").GetInt32());
        Assert.Equal(1, smtp.GetProperty("maxMessageSizeMb").GetInt32());
    }
}
