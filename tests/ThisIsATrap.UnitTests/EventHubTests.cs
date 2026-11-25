using System.Text.Json;
using ThisIsATrap;

namespace ThisIsATrap.Tests;

public sealed class EventHubTests
{
    [Fact]
    public async Task Publish_reaches_every_subscriber_as_camel_case_json()
    {
        var hub = new EventHub();
        var (_, a) = hub.Subscribe();
        var (_, b) = hub.Subscribe();

        hub.Publish("message", new { MessageId = "x" });

        foreach (var reader in new[] { a, b })
        {
            using var doc = JsonDocument.Parse(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("message", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("x", doc.RootElement.GetProperty("payload").GetProperty("messageId").GetString());
        }
        Assert.Equal(2, hub.ConnectedClients);
    }

    [Fact]
    public void Publish_without_payload_sends_null_payload()
    {
        var hub = new EventHub();
        var (_, reader) = hub.Subscribe();
        hub.Publish("changed");

        Assert.True(reader.TryRead(out var data));
        using var doc = JsonDocument.Parse(data!);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("payload").ValueKind);
    }

    [Fact]
    public void Unsubscribe_completes_the_reader_and_is_idempotent()
    {
        var hub = new EventHub();
        var (id, reader) = hub.Subscribe();

        hub.Unsubscribe(id);
        hub.Unsubscribe(id);

        Assert.Equal(0, hub.ConnectedClients);
        Assert.True(reader.Completion.IsCompleted);
    }

    [Fact]
    public void Slow_subscriber_keeps_only_the_newest_100_events()
    {
        var hub = new EventHub();
        var (_, reader) = hub.Subscribe();
        for (var i = 0; i < 150; i++) hub.Publish("n", i);

        var received = new List<int>();
        while (reader.TryRead(out var data))
            received.Add(JsonDocument.Parse(data).RootElement.GetProperty("payload").GetInt32());

        Assert.Equal(100, received.Count);
        Assert.Equal(50, received[0]);
        Assert.Equal(149, received[^1]);
    }
}
