using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TotallyHot.ArcRouter.Telemetry;

/// <summary>
/// Groups requests into sessions by matching a request's "messages" array against previously-tracked
/// ones, for clients that send no session identifier of any kind (e.g. GitHub Copilot's OpenAI-compatible
/// model providers - see docs/router/telemetry.md's "Client integration note"). A request whose messages
/// are a previously-tracked conversation's messages plus new ones appended is treated as the next turn
/// of that conversation.
/// </summary>
public interface IConversationContinuityMatcher
{
    /// <summary>
    /// Matches <paramref name="messages"/> against tracked conversations, or starts tracking a new one.
    /// Always returns a usable session id - this owns id synthesis for the "no explicit session id
    /// found" path, so callers should use this instead of generating their own fallback GUID.
    /// </summary>
    /// <param name="messages">The parsed "messages" array from the request body, or <see langword="null"/> if absent/not an array.</param>
    string MatchOrTrack(JsonArray? messages);
}

/// <summary>
/// Default <see cref="IConversationContinuityMatcher"/>. In-memory and process-lifetime only, matching
/// <see cref="ConversationTurnTracker"/>'s "no persistence beyond the process" model. Tracked
/// conversations are evicted after <see cref="StalenessWindow"/> of inactivity so (a) memory doesn't
/// grow unboundedly over a long-running proxy process, and (b) a conversation from hours ago can't get
/// accidentally matched against an unrelated new one that happens to share an opening exchange.
/// </summary>
/// <remarks>
/// This is a heuristic, not a real session concept, and <see cref="SessionIdResolver"/> (which reads an
/// explicit identifier the client actually sent) is always tried first and takes priority over this.
/// Known limitations:
/// <list type="bullet">
/// <item>False positive: two genuinely unrelated conversations that happen to open with an identical
/// exchange (e.g. the same fixed system prompt and a coincidentally identical first message) would be
/// merged into one tracked session.</item>
/// <item>False negative: a client that edits or regenerates earlier messages, rather than only ever
/// appending new ones, won't match its own prior turns (the prefix no longer matches exactly) and
/// starts a new tracked conversation instead.</item>
/// </list>
/// Each message is fingerprinted as a SHA-256 hash of its canonical JSON, rather than storing the
/// message content itself, so full prompt/response text isn't retained in memory beyond what the
/// request/response pipeline already holds transiently.
/// </remarks>
public sealed class MessageHistoryContinuityMatcher : IConversationContinuityMatcher
{
    private static readonly TimeSpan StalenessWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A single tracked conversation: the synthesized session id assigned to it, the SHA-256
    /// fingerprints of its messages seen so far, and when it was last matched, used to evict stale
    /// entries after <see cref="StalenessWindow"/>.
    /// </summary>
    /// <param name="SessionId">The synthesized session id for this conversation.</param>
    /// <param name="MessageFingerprints">The SHA-256 fingerprints of the conversation's messages, in order.</param>
    /// <param name="LastSeenUtc">The UTC timestamp of the last request matched to this conversation.</param>
    private sealed record TrackedConversation(string SessionId, IReadOnlyList<string> MessageFingerprints, DateTimeOffset LastSeenUtc);

    private readonly object _lock = new();
    private readonly List<TrackedConversation> _tracked = [];
    private readonly TimeProvider _timeProvider;

    /// <param name="timeProvider">Optional; defaults to <see cref="TimeProvider.System"/>. Overridable in tests to control staleness-window expiry.</param>
    public MessageHistoryContinuityMatcher(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string MatchOrTrack(JsonArray? messages)
    {
        var now = _timeProvider.GetUtcNow();
        var fingerprints = Fingerprint(messages);

        lock (_lock)
        {
            _tracked.RemoveAll(t => now - t.LastSeenUtc > StalenessWindow);

            TrackedConversation? bestMatch = null;
            foreach (var candidate in _tracked)
            {
                if (candidate.MessageFingerprints.Count > 0 &&
                    candidate.MessageFingerprints.Count < fingerprints.Count &&
                    IsPrefix(candidate.MessageFingerprints, fingerprints) &&
                    (bestMatch is null || candidate.MessageFingerprints.Count > bestMatch.MessageFingerprints.Count))
                {
                    bestMatch = candidate;
                }
            }

            if (bestMatch is not null)
            {
                _tracked.Remove(bestMatch);
                _tracked.Add(bestMatch with { MessageFingerprints = fingerprints, LastSeenUtc = now });
                return bestMatch.SessionId;
            }

            var sessionId = Guid.NewGuid().ToString("N");
            if (fingerprints.Count > 0)
            {
                _tracked.Add(new TrackedConversation(sessionId, fingerprints, now));
            }

            return sessionId;
        }
    }

    /// <summary>
    /// Determines whether every fingerprint in <paramref name="prefix"/> matches the fingerprint at the
    /// same position in <paramref name="full"/>, in order.
    /// </summary>
    private static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> full)
    {
        for (var i = 0; i < prefix.Count; i++)
        {
            if (!string.Equals(prefix[i], full[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes a SHA-256 fingerprint of each message's canonical JSON representation, returning an
    /// empty list when <paramref name="messages"/> is <see langword="null"/>.
    /// </summary>
    private static List<string> Fingerprint(JsonArray? messages)
    {
        if (messages is null)
        {
            return [];
        }

        var fingerprints = new List<string>(messages.Count);
        foreach (var message in messages)
        {
            var canonical = message?.ToJsonString() ?? string.Empty;
            fingerprints.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        }

        return fingerprints;
    }
}

