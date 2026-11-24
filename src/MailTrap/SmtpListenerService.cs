using System.Buffers;
using System.Net;
using Microsoft.Extensions.Options;
using MimeKit;
using SmtpServer;
using SmtpServer.Authentication;
using SmtpServer.Net;
using SmtpServer.Protocol;
using SmtpServer.Storage;

namespace MailTrap;

/// <summary>
/// Runs a real SMTP server inside the web app. Every message is accepted and stored locally;
/// nothing is ever relayed or delivered anywhere else.
/// </summary>
public sealed class SmtpListenerService(
    IOptions<MailTrapOptions> options,
    MailStore store,
    EventHub events,
    ILogger<SmtpListenerService> log) : BackgroundService
{
    public static string Status { get; private set; } = "starting";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var serverOptions = new SmtpServerOptionsBuilder()
            .ServerName(o.ServerName)
            .MaxMessageSize(o.MaxMessageSizeMb * 1024 * 1024)
            .Endpoint(b => b
                .Endpoint(new IPEndPoint(IPAddress.Any, o.SmtpPort))
                .AuthenticationRequired(false)
                .AllowUnsecureAuthentication(true))
            .Build();

        var services = new SmtpServer.ComponentModel.ServiceProvider();
        services.Add(new TrapMessageStore(store, events, log));
        services.Add(new AcceptAnyUser());

        // IIS can briefly run the old and new worker side by side during a recycle, so the port
        // may still be held for a moment. Keep retrying instead of giving up.
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var server = new SmtpServer.SmtpServer(serverOptions, services);
            try
            {
                log.LogInformation("SMTP listener starting on port {Port}", o.SmtpPort);
                Status = $"listening on port {o.SmtpPort}";
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
                Status = $"error: {ex.Message}";
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

    private sealed class TrapMessageStore(MailStore store, EventHub events, ILogger log) : MessageStore
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
                    clientIp);

                await store.AddAsync(record, raw, cancellationToken);
                log.LogInformation("Trapped message {Id} from {From} to {To}: {Subject}", record.Id, record.From, record.To, record.Subject);

                events.Publish("message", new MessageSummary(record.Id, record.ReceivedUtc, record.From, record.To, record.Subject,
                    record.Preview, record.Size, record.AttachmentCount, false));
                return SmtpResponse.Ok;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to store incoming message");
                return new SmtpResponse(SmtpReplyCode.TransactionFailed, "Could not store message");
            }
        }
    }
}
