using System.Collections.Concurrent;
using DigiAhan.CDR.Receiver.Models;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class AgentEventStore
{
    private readonly ConcurrentDictionary<string, AgentEventEnvelope> _events = new();
    private long _sequence;

    public AgentEventEnvelope Put(string extension, AgentCustomerCard card)
    {
        var envelope = new AgentEventEnvelope(
            Interlocked.Increment(ref _sequence),
            card);
        _events[extension] = envelope;
        return envelope;
    }

    public AgentEventEnvelope? Get(string extension)
        => _events.TryGetValue(extension, out var value) ? value : null;
}
