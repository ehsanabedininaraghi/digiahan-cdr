using System.Collections.Concurrent;
using DigiAhan.CDR.Receiver.Models;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class AgentCallStateStore
{
    private readonly ConcurrentDictionary<string, SellerCallLifecycle> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public SellerCallLifecycle RegisterRing(string? linkedId, DateTime eventAtUtc)
    {
        if (string.IsNullOrWhiteSpace(linkedId))
            return new SellerCallLifecycle("RINGING", null, null, null, 0);

        return _states.AddOrUpdate(
            linkedId.Trim(),
            _ => new SellerCallLifecycle("RINGING", null, null, null, 0),
            (_, current) => current.State == "ENDED"
                ? new SellerCallLifecycle("RINGING", null, null, null, 0)
                : current);
    }

    public SellerCallLifecycle Update(VoipCallStatusRequest request)
    {
        var key = request.LinkedId.Trim();
        var state = request.State.Trim().ToUpperInvariant();
        if (state is not ("RINGING" or "ANSWERED" or "ENDED"))
            throw new ArgumentException("State must be RINGING, ANSWERED or ENDED.");

        var eventAt = request.EventTimeUtc ?? DateTime.UtcNow;
        return _states.AddOrUpdate(
            key,
            _ => Create(state, request.Extension, eventAt, null),
            (_, current) => Create(state, request.Extension, eventAt, current));
    }

    public SellerCallLifecycle? Get(string? linkedId)
    {
        if (string.IsNullOrWhiteSpace(linkedId)) return null;
        if (!_states.TryGetValue(linkedId.Trim(), out var state)) return null;
        if ((state.EndedAtUtc ?? state.AnsweredAtUtc ?? DateTime.UtcNow) < DateTime.UtcNow.AddHours(-4))
        {
            _states.TryRemove(linkedId.Trim(), out _);
            return null;
        }
        return state;
    }

    private static SellerCallLifecycle Create(
        string state,
        string? extension,
        DateTime eventAt,
        SellerCallLifecycle? current)
    {
        var answeredExtension = string.IsNullOrWhiteSpace(extension)
            ? current?.AnsweredExtension
            : extension.Trim();
        return state switch
        {
            "ANSWERED" => new SellerCallLifecycle(
                state, answeredExtension, current?.AnsweredAtUtc ?? eventAt, null, 0),
            "ENDED" => new SellerCallLifecycle(
                state, answeredExtension, current?.AnsweredAtUtc,
                eventAt,
                current?.AnsweredAtUtc is DateTime answered
                    ? Math.Max(0, (int)(eventAt - answered).TotalSeconds)
                    : current?.TalkSeconds ?? 0),
            _ => new SellerCallLifecycle("RINGING", null, null, null, 0)
        };
    }
}
