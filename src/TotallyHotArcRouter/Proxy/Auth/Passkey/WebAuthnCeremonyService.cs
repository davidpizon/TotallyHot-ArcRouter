using Fido2NetLib;
using Fido2NetLib.Objects;
using System.Collections.Concurrent;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Production WebAuthn ceremonies using Fido2NetLib (ADR-0020): RP ID <c>localhost</c>, HTTPS origin from
/// <see cref="WebInterfaceOptions.Port"/>, user verification required, no attestation. Challenges always
/// come from <see cref="ChallengeStore"/> and are consumed before cryptographic verification.
/// </summary>
public sealed class WebAuthnCeremonyService : IWebAuthnCeremonyService
{
    private static readonly byte[] RpUserId = "totallyhot-arcrouter-passkey-user"u8.ToArray();

    private readonly IFido2 _fido2;
    private readonly ChallengeStore _challengeStore;
    private readonly IPasskeyCredentialStore _credentialStore;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CredentialCreateOptions> _pendingRegistration = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AssertionOptions> _pendingAssertion = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="WebAuthnCeremonyService"/> class.</summary>
    /// <param name="webInterfaceOptions">Supplies the dashboard HTTPS port for the WebAuthn origin.</param>
    /// <param name="challengeStore">Issues and consumes ceremony challenges.</param>
    /// <param name="credentialStore">Persists enrolled credentials.</param>
    /// <param name="timeProvider">Optional clock override for tests.</param>
    public WebAuthnCeremonyService(
        TotallyHot.ArcRouter.Proxy.WebInterfaceOptions webInterfaceOptions,
        ChallengeStore challengeStore,
        IPasskeyCredentialStore credentialStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(webInterfaceOptions);
        ArgumentNullException.ThrowIfNull(challengeStore);
        ArgumentNullException.ThrowIfNull(credentialStore);
        _challengeStore = challengeStore;
        _credentialStore = credentialStore;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var origin = new Uri($"https://localhost:{webInterfaceOptions.Port}", UriKind.Absolute);
        _fido2 = new Fido2(new Fido2Configuration
        {
            RPID = "localhost",
            RPName = "TotallyHot Arc Router",
            Origins = new HashSet<string> { origin.AbsoluteUri.TrimEnd('/') },
            TimestampDriftTolerance = 60_000,
        });
    }

    /// <inheritdoc/>
    public string BeginRegistration()
    {
        var challenge = _challengeStore.Issue();
        var user = new Fido2User
        {
            Name = "administrator",
            Id = RpUserId,
            DisplayName = "Arc Router Administrator",
        };

        var exclude = _credentialStore.List()
            .Select(c => new PublicKeyCredentialDescriptor(c.Id))
            .ToList();

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = user,
            ExcludeCredentials = exclude,
            AttestationPreference = AttestationConveyancePreference.None,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                UserVerification = UserVerificationRequirement.Required,
            },
        });

        options.Challenge = challenge;
        var key = PasskeyEncoding.ToBase64Url(challenge);
        _pendingRegistration[key] = options;
        return options.ToJson();
    }

    /// <inheritdoc/>
    public PasskeyCredentialRecord FinishRegistration(string attestationJson, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attestationJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationJson)
                       ?? throw new InvalidOperationException("Attestation response JSON was invalid.");

        var challenge = ExtractChallenge(response.Response.ClientDataJson);
        if (!_challengeStore.TryConsume(challenge, out _))
            throw new InvalidOperationException("Registration challenge was missing or already used.");

        var key = PasskeyEncoding.ToBase64Url(challenge);
        if (!_pendingRegistration.TryRemove(key, out var originalOptions))
            throw new InvalidOperationException("Registration ceremony was not started.");

        var result = _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
        {
            AttestationResponse = response,
            OriginalOptions = originalOptions,
            IsCredentialIdUniqueToUserCallback = (args, _) =>
            {
                var exists = _credentialStore.List().Any(c => c.Id.AsSpan().SequenceEqual(args.CredentialId));
                return Task.FromResult(!exists);
            },
        }).GetAwaiter().GetResult();

        var record = new PasskeyCredentialRecord(
            Id: result.Id,
            PublicKey: result.PublicKey,
            SignCount: result.SignCount,
            Name: name,
            CreatedAtUtc: _timeProvider.GetUtcNow(),
            BackupEligible: result.IsBackupEligible,
            BackupState: result.IsBackedUp);

        _credentialStore.Add(record);
        return record;
    }

    /// <inheritdoc/>
    public string BeginAssertion(string operation, string parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        parameters ??= string.Empty;

        var credentials = _credentialStore.List();
        if (credentials.Count == 0)
            throw new InvalidOperationException("No passkeys are enrolled.");

        var challenge = _challengeStore.Issue(operation, parameters);
        var allowed = credentials.Select(c => new PublicKeyCredentialDescriptor(c.Id)).ToList();
        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification = UserVerificationRequirement.Required,
        });
        options.Challenge = challenge;

        var key = PasskeyEncoding.ToBase64Url(challenge);
        _pendingAssertion[key] = options;
        return options.ToJson();
    }

    /// <inheritdoc/>
    public string FinishAssertion(string assertionJson, string operation, string parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assertionJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        parameters ??= string.Empty;

        var response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionJson)
                       ?? throw new InvalidOperationException("Assertion response JSON was invalid.");

        var challenge = ExtractChallenge(response.Response.ClientDataJson);
        if (!_challengeStore.TryConsume(challenge, out var pending) || pending is null)
            throw new InvalidOperationException("Assertion challenge was missing or already used.");

        if (!string.Equals(pending.Operation, operation, StringComparison.Ordinal) ||
            !string.Equals(pending.Parameters, parameters, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Assertion challenge binding did not match the operation.");
        }

        var key = PasskeyEncoding.ToBase64Url(challenge);
        if (!_pendingAssertion.TryRemove(key, out var originalOptions))
            throw new InvalidOperationException("Assertion ceremony was not started.");

        var credentialId = response.RawId is { Length: > 0 }
            ? response.RawId
            : PasskeyEncoding.FromBase64Url(response.Id);
        var credential = _credentialStore.List()
                             .FirstOrDefault(c => c.Id.AsSpan().SequenceEqual(credentialId))
                         ?? throw new InvalidOperationException("Unknown credential.");

        var assertionResult = _fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = response,
            OriginalOptions = originalOptions,
            StoredPublicKey = credential.PublicKey,
            StoredSignatureCounter = credential.SignCount,
            IsUserHandleOwnerOfCredentialIdCallback = static (_, _) => Task.FromResult(true),
        }).GetAwaiter().GetResult();

        if (assertionResult.SignCount > 0 && assertionResult.SignCount <= credential.SignCount)
            throw new InvalidOperationException("Authenticator sign counter did not increase.");

        var updated = credential with
        {
            SignCount = assertionResult.SignCount > 0 ? assertionResult.SignCount : credential.SignCount,
        };
        _credentialStore.Update(updated);
        return credential.Name;
    }

    private static byte[] ExtractChallenge(byte[] clientDataJson)
    {
        using var doc = JsonDocument.Parse(clientDataJson);
        var challengeProp = doc.RootElement.GetProperty("challenge").GetString()
                            ?? throw new InvalidOperationException("clientDataJSON missing challenge.");
        return PasskeyEncoding.FromBase64Url(challengeProp);
    }
}
