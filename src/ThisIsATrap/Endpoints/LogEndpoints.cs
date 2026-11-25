using System.Text;
using ThisIsATrap.Logs;

namespace ThisIsATrap.Endpoints;

/// <summary>The in-app log viewer and the SMTP client allowlist.</summary>
public static class LogEndpoints
{
    public static void MapLogs(this RouteGroupBuilder api)
    {
        // ?after=<seq> returns only newer entries, for live tailing.
        api.MapGet("/logs", (LogBuffer logs, long? after, string? level, string? q, string? category, int? take) =>
            Results.Ok(logs.Query(after ?? 0, level, q, category, take ?? 500)));

        api.MapGet("/logs/download", (LogBuffer logs, string? level, string? q, string? category) =>
        {
            var page = logs.Query(0, level, q, category, 100_000);
            var text = new StringBuilder();
            foreach (var e in page.Entries)
            {
                text.Append(e.Utc.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'")).Append(' ')
                    .Append(e.Level.PadRight(11)).Append(' ').Append(e.Category).Append(": ").AppendLine(e.Message);
                if (e.Exception is not null) text.AppendLine(e.Exception);
            }
            return Results.File(Encoding.UTF8.GetBytes(text.ToString()), "text/plain; charset=utf-8", $"thisisatrap-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
        });

        api.MapDelete("/logs", (LogBuffer logs) => Results.Ok(new { removed = logs.Clear() }));

        // ---- SMTP client allowlist
        api.MapGet("/settings/smtp-access", (SmtpAccess access, HttpContext http) => Results.Ok(new
        {
            settings = access.Get(),
            yourAddress = Normalize(http.Connection.RemoteIpAddress),
        }));

        api.MapPut("/settings/smtp-access", (SmtpAccessSettings body, SmtpAccess access, ILogger<SmtpAccess> log) =>
        {
            try
            {
                var saved = access.Save(body);
                log.LogInformation("SMTP allowlist {State}: {Count} entr(ies){Loopback}", saved.Enabled ? "enabled" : "disabled",
                    saved.Allow.Count, saved.AlwaysAllowLoopback ? " + localhost" : "");
                return Results.Ok(saved);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static string? Normalize(System.Net.IPAddress? ip) => ip is null ? null : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
}
