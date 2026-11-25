namespace ThisIsATrap;

/// <summary>Static configuration (appsettings.json / environment). Purge rules live in the database so the UI can edit them.</summary>
public sealed class TrapOptions
{
    public const string Section = "ThisIsATrap";

    /// <summary>Section name used before the rename. Still read so older appsettings / env vars keep working.</summary>
    public const string LegacySection = "MailTrap";

    /// <summary>
    /// Mail sent with an SMTP username (or an X-Trap-Inbox header) that matches no inbox creates one.
    /// When false, such mail goes to the Default inbox instead.
    /// </summary>
    public bool AutoCreateInboxes { get; set; } = true;

    /// <summary>Port the SMTP listener binds to. 25 is the standard SMTP port.</summary>
    public int SmtpPort { get; set; } = 25;

    /// <summary>
    /// Port clients should connect to, when it differs from <see cref="SmtpPort"/> (e.g. a Docker
    /// container listening on 2525 published as 25 on the host). Only affects what the UI shows.
    /// </summary>
    public int? PublicSmtpPort { get; set; }

    /// <summary>Host name announced in the SMTP greeting.</summary>
    public string ServerName { get; set; } = Environment.MachineName;

    /// <summary>Largest message accepted, in megabytes.</summary>
    public int MaxMessageSizeMb { get; set; } = 50;

    /// <summary>Where the SQLite index and raw .eml files are stored. Relative paths resolve against the app folder.</summary>
    public string DataPath { get; set; } = "App_Data";
}

/// <summary>User-editable auto-purge rules.</summary>
public sealed record PurgeSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Delete messages older than this many hours (0 = no age limit).</summary>
    public int MaxAgeHours { get; init; } = 72;

    /// <summary>Keep at most this many messages, newest first (0 = no count limit).</summary>
    public int MaxMessages { get; init; } = 1000;

    /// <summary>How often the purge job runs, in minutes.</summary>
    public int IntervalMinutes { get; init; } = 15;
}
