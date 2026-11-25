using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using ThisIsATrap.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MimeKit;

[assembly: Trait("Category", "Integration")]

namespace ThisIsATrap.IntegrationTests;

/// <summary>
/// The real app, in-process, with its SMTP listener bound to a free localhost port and a throwaway data folder.
/// HTTP goes through the in-memory test server; SMTP goes over a real socket.
/// </summary>
public class TrapApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly TempDir _data = new();

    public int SmtpPort { get; } = FreePort();
    public string DataPath => _data.Path;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Extra configuration for derived fixtures.</summary>
    protected virtual IDictionary<string, string?> Settings => new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ThisIsATrap:SmtpPort"] = SmtpPort.ToString(),
                ["ThisIsATrap:DataPath"] = _data.Path,
                ["ThisIsATrap:ServerName"] = "thisisatrap.test",
                ["ThisIsATrap:MaxMessageSizeMb"] = "1",
                ["Logging:LogLevel:Default"] = "Warning",
            };
            foreach (var (k, v) in Settings) settings[k] = v;
            config.AddInMemoryCollection(settings);
        });
    }

    public async ValueTask InitializeAsync()
    {
        _ = Server; // starts the host and its background services
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, SmtpPort);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _data.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<SmtpClient> ConnectSmtpAsync(string? user = null, string? password = null)
    {
        var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", SmtpPort, SecureSocketOptions.None);
        if (user is not null) await client.AuthenticateAsync(user, password ?? "anything");
        return client;
    }

    public async Task SendAsync(MimeMessage message, string? user = null, IEnumerable<string>? envelopeTo = null)
    {
        using var client = await ConnectSmtpAsync(user);
        if (envelopeTo is null) await client.SendAsync(FormatOptions.Default, new MailKit.SendRequest(message));
        else await client.SendAsync(FormatOptions.Default, new MailKit.SendRequest(message, MailboxAddress.Parse("sender@example.com"), envelopeTo.Select(MailboxAddress.Parse)));
        await client.DisconnectAsync(true);
    }

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        using var http = CreateClient();
        var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>Finds a stored message by its (unique) subject, waiting briefly for the SMTP handler to finish.</summary>
    public async Task<JsonElement> FindAsync(string subject)
    {
        for (var i = 0; i < 50; i++)
        {
            var list = await GetJsonAsync("/api/messages?q=" + Uri.EscapeDataString(subject));
            foreach (var item in list.GetProperty("items").EnumerateArray())
                if (item.GetProperty("subject").GetString() == subject) return item;
            await Task.Delay(50);
        }
        throw new InvalidOperationException($"Message '{subject}' never arrived.");
    }

    public async Task ClearAsync()
    {
        using var http = CreateClient();
        (await http.DeleteAsync("/api/messages")).EnsureSuccessStatusCode();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<TrapApp>
{
    public const string Name = "app";
}
