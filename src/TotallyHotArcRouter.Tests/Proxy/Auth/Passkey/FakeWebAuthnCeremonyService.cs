using System.Text.Json;
using TotallyHot.ArcRouter.Proxy.Auth.Passkey;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

/// <summary>
/// Test double for <see cref="IWebAuthnCeremonyService"/> that avoids Fido2 crypto: ceremonies embed
/// challenges from <see cref="ChallengeStore"/>, and assertions succeed when the JSON contains the
/// pending challenge hex and a known credential exists.
/// </summary>
public sealed class FakeWebAuthnCeremonyService : IWebAuthnCeremonyService
{
    private readonly ChallengeStore _challengeStore;
    private readonly IPasskeyCredentialStore _credentialStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="FakeWebAuthnCeremonyService"/> class.</summary>
    public FakeWebAuthnCeremonyService(
        ChallengeStore challengeStore,
        IPasskeyCredentialStore credentialStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(challengeStore);
        ArgumentNullException.ThrowIfNull(credentialStore);
        _challengeStore = challengeStore;
        _credentialStore = credentialStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string BeginRegistration()
    {
        var challenge = _challengeStore.Issue();
        return JsonSerializer.Serialize(new
        {
            challenge = PasskeyEncoding.ToBase64Url(challenge),
            rp = new { id = "localhost", name = "test" },
        });
    }

    /// <inheritdoc/>
    public PasskeyCredentialRecord FinishRegistration(string attestationJson, string name)
    {
        var challenge = ExtractChallengeHex(attestationJson);
        if (!_challengeStore.TryConsume(PasskeyEncoding.FromBase64Url(challenge), out _))
            throw new InvalidOperationException("Challenge missing.");

        var record = new PasskeyCredentialRecord(
            Id: [1, 2, 3],
            PublicKey: [4, 5, 6],
            SignCount: 0,
            Name: name,
            CreatedAtUtc: _timeProvider.GetUtcNow(),
            BackupEligible: false,
            BackupState: false);
        _credentialStore.Add(record);
        return record;
    }

    /// <inheritdoc/>
    public string BeginAssertion(string operation, string? parameters)
    {
        var challenge = _challengeStore.Issue(operation, parameters);
        var key = PasskeyEncoding.ToBase64Url(challenge);
        return JsonSerializer.Serialize(new { challenge = key });
    }

    /// <inheritdoc/>
    public string FinishAssertion(string assertionJson, string operation, string? parameters)
    {
        var challengeKey = ExtractChallengeHex(assertionJson);
        if (!_challengeStore.TryConsume(PasskeyEncoding.FromBase64Url(challengeKey), out var pending) ||
            pending is null)
        {
            throw new InvalidOperationException("Challenge missing.");
        }

        // Same binding rule as WebAuthnCeremonyService: a challenge begun for one operation and parameters string
        // cannot finish another, which is what keeps a pending approval bound to its own digest.
        if (!string.Equals(pending.Operation, operation, StringComparison.Ordinal) ||
            !string.Equals(pending.Parameters, parameters ?? string.Empty, StringComparison.Ordinal))
            throw new InvalidOperationException("Operation mismatch.");

        var credential = _credentialStore.List().FirstOrDefault()
                       ?? throw new InvalidOperationException("No credential.");
        return credential.Name;
    }

    private static string ExtractChallengeHex(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("challenge").GetString()
               ?? throw new InvalidOperationException("Missing challenge.");
    }
}
