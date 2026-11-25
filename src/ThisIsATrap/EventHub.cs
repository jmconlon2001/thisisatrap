using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace ThisIsATrap;

/// <summary>Fan-out of server events to every connected browser (Server-Sent Events).</summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public (Guid Id, ChannelReader<string> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel)) channel.Writer.TryComplete();
    }

    public void Publish(string type, object? payload = null)
    {
        var data = JsonSerializer.Serialize(new { type, payload }, Json);
        foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(data);
    }

    public int ConnectedClients => _subscribers.Count;
}
