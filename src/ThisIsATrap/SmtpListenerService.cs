using System.Buffers;
using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Options;
using MimeKit;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace ThisIsATrap;

/// <summary>
/// Runs a real SMTP server inside the web app. Every message is accepted and stored locally;
/// nothing is ever relayed or delivered anywhere else.
/// </summary>
public sealed class SmtpListenerService(
    IOptions<TrapOptions> options,
    MailStore store,
    EventHub events,
    Health.RuntimeStats stats,
    SmtpTls tls,
    SmtpRules rules,
    WebhookService webhooks,
    TimeProvider time,
    ILogger<SmtpListenerService> log) : BackgroundService
{
    private const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>Builds the server options: the main endpoint (with STARTTLS when enabled) plus an optional implicit-TLS endpoint.</summary>
    private ISmtpServerOptions BuildOptions(TrapOptions o)
    {
        var cert = tls.Certificate; // null when TLS is off
        var builder = new SmtpServerOptionsBuilder()
            .ServerName(o.ServerName)
            .MaxMessageSize(o.MaxMessageSizeMb * 1024 * 1024)
            .Endpoint(b =>
            {
                b.Endpoint(new IPEndPoint(IPAddress.Any, o.SmtpPort))
                    .AuthenticationRequired(false)
                    .AllowUnsecureAuthentication(true);
                if (o.Tls.StartTls && cert is not null) b.Certificate(cert).SupportedSslProtocols(Protocols);
            });
        if (o.Tls.SmtpsPort is { } smtps && cert is not null)
        {
            builder.Endpoint(b => b
                .Endpoint(new IPEndPoint(IPAddress.Any, smtps))
                .IsSecure(true)
                .Certificate(cert)
                .SupportedSslProtocols(Protocols)
                .AuthenticationRequired(false)
                .AllowUnsecureAuthentication(true));
        }
        return builder.Build();
    }

    public static string ListeningStatus(TrapOptions o) =>
        $"listening on port {o.SmtpPort}" + (o.Tls.StartTls ? " (STARTTLS)" : "") + (o.Tls.SmtpsPort is { } p ? $", {p} (TLS)" : "");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var services = new SmtpServer.ComponentModel.ServiceProvider();
        services.Add(new TrapMessageStore(store, events, stats, webhooks, o.AutoCreateInboxes, log));
        services.Add(new AcceptAnyUser());
        services.Add(new SmtpRuleFilter(rules, events, stats, time, log));

        // IIS can briefly run the old and new worker side by side during a recycle, so the port
        // may still be held for a moment. Keep retrying instead of giving up.
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var server = new SmtpServer.SmtpServer(BuildOptions(o), services);
                log.LogInformation("SMTP listener starting: {Status}", ListeningStatus(o));
                stats.SmtpStatus = ListeningStatus(o);
                await server.StartAsync(stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                stats.SmtpStatus = $"error: {ex.Message}";
                log.LogError(ex, "SMTP listener failed on port {Port} (attempt {Attempt}); retrying", o.SmtpPort, attempt);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private sealed class AcceptAnyUser : IUserAuthenticator
    {
        public Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class TrapMessageStore(MailStore store, EventHub events, Health.RuntimeStats stats, WebhookService webhooks, bool autoCreateInboxes, ILogger log) : MessageStore
    {
        public override async Task<SmtpResponse> SaveAsync(ISessionContext context, IMessageTransaction transaction, ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                await using var raw = new MemoryStream((int)buffer.Length);
                foreach (var segment in buffer) raw.Write(segment.Span);
                raw.Position = 0;

                var message = await MimeMessage.LoadAsync(raw, cancellationToken);
                raw.Position = 0;

                var envelopeTo = string.Join(", ", transaction.To.Select(m => $"{m.User}@{m.Host}"));
                var envelopeFrom = transaction.From is { } f && !string.IsNullOrEmpty(f.User) ? $"{f.User}@{f.Host}" : "";
                var clientIp = context.Properties.TryGetValue(EndpointListener.RemoteEndPointKey, out var ep) && ep is IPEndPoint ip
                    ? ip.Address.ToString() : "";

                // Inbox routing: SMTP username first, then the X-Trap-Inbox header, else Default.
                var routingKey = context.Authentication is { IsAuthenticated: true, User: { Length: > 0 } user }
                    ? user
                    : message.Headers[Inbox.RoutingHeader];
                var (inbox, created) = store.ResolveInbox(routingKey, autoCreateInboxes);
                if (created)
                {
                    log.LogInformation("Created inbox {Slug}", inbox.Slug);
                    events.Publish("inboxes");
                }

                var from = MessageParser.Addresses(message.From);
                var to = MessageParser.Addresses(message.To);
                var record = new MessageRecord(
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow,
                    from.Length > 0 ? from : envelopeFrom,
                    to.Length > 0 ? to : envelopeTo,
                    MessageParser.Addresses(message.Cc),
                    message.Subject ?? "",
                    MessageParser.Preview(message),
                    buffer.Length,
                    MessageParser.CountAttachments(message),
                    false,
                    envelopeFrom,
                    envelopeTo,
                    clientIp,
                    inbox.Id);

                await store.AddAsync(record, raw, cancellationToken);
                stats.MessageReceived();
                log.LogInformation("Trapped message {Id} in {Inbox} from {From} to {To}: {Subject}", record.Id, inbox.Slug, record.From, record.To, record.Subject);

                var summary = new MessageSummary(record.Id, record.ReceivedUtc, record.From, record.To, record.Subject,
                    record.Preview, record.Size, record.AttachmentCount, false, inbox.Slug);
                events.Publish("message", summary);
                webhooks.Notify(summary, inbox);
                return SmtpResponse.Ok;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to store incoming message");
                stats.MessageRejected();
                return new SmtpResponse(SmtpReplyCode.TransactionFailed, "Could not store message");
            }
        }
    }
}
