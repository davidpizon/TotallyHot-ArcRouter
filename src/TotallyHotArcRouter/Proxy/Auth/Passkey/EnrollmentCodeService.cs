using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Mints and validates single-use enrollment codes for passkey registration (ADR-0020). Only a SHA-256
/// hash of the code is persisted so a leaked <c>secrets.dat</c> dump cannot enroll a rogue credential;
/// five failed guesses invalidate the active code.
/// </summary>
public sealed class EnrollmentCodeService
{
    /// <summary>Secret-store key for the active enrollment code metadata.</summary>
    public const string StoreKey = "passkey.enrollment-code.v1";

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private const int MaxFailures = 5;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ISecretReader _reader;
    private readonly ISecretWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();

    /// <summary>Initializes a new instance of the <see cref="EnrollmentCodeService"/> class.</summary>
    /// <param name="reader">Reads the protected secret store.</param>
    /// <param name="writer">Writes the protected secret store.</param>
    /// <param name="timeProvider">Clock for TTL checks; defaults to <see cref="TimeProvider.System"/>.</param>
    public EnrollmentCodeService(ISecretReader reader, ISecretWriter writer, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        _reader = reader;
        _writer = writer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Initializes a new instance backed by one <see cref="ProtectedSecretStore"/>.</summary>
    /// <param name="store">The protected secret store.</param>
    /// <param name="timeProvider">Optional clock override for tests.</param>
    public EnrollmentCodeService(ProtectedSecretStore store, TimeProvider? timeProvider = null)
        : this(reader: store, writer: store, timeProvider: timeProvider)
    {
    }

    /// <summary>
    /// Generates a fresh 128-bit enrollment code, stores its hash with a ten-minute expiry, and returns
    /// the grouped base32 form shown to the operator once.
    /// </summary>
    public string Mint()
    {
        var entropy = RandomNumberGenerator.GetBytes(16);
        var display = PasskeyEncoding.FormatEnrollmentCode(entropy);
        var normalized = PasskeyEncoding.NormalizeEnrollmentCode(display);
        var hash = PasskeyEncoding.HashEnrollmentCode(normalized);
        var payload = new EnrollmentCodePayload(
            CodeHashBase64: Convert.ToBase64String(hash),
            ExpiresAtUtc: _timeProvider.GetUtcNow().Add(Ttl),
            Failures: 0);

        lock (_lock)
        {
            _writer.Write(name: StoreKey, value: JsonSerializer.Serialize(payload, JsonOptions));
        }

        return display;
    }

    /// <summary>
    /// Validates <paramref name="code"/> in constant time against the stored hash when still within TTL
    /// and under the failure budget, and spends it in the same critical section: two concurrent requests
    /// cannot both succeed with one code, and a code minted meanwhile is never deleted by a late
    /// invalidation.
    /// </summary>
    /// <returns><see langword="true"/> when the code matched and has now been consumed.</returns>
    public bool TryConsume(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = PasskeyEncoding.NormalizeEnrollmentCode(code);
        var presentedHash = PasskeyEncoding.HashEnrollmentCode(normalized);

        lock (_lock)
        {
            if (!TryLoad(out var payload) || payload is null) return false;

            if (_timeProvider.GetUtcNow() >= payload.ExpiresAtUtc)
            {
                _writer.Delete(name: StoreKey);
                return false;
            }

            var storedHash = Convert.FromBase64String(payload.CodeHashBase64);
            var match = storedHash.Length == presentedHash.Length &&
                        CryptographicOperations.FixedTimeEquals(storedHash, presentedHash);

            if (match)
            {
                _writer.Delete(name: StoreKey);
                return true;
            }

            payload = payload with { Failures = payload.Failures + 1 };
            if (payload.Failures >= MaxFailures)
            {
                _writer.Delete(name: StoreKey);
                return false;
            }

            _writer.Write(name: StoreKey, value: JsonSerializer.Serialize(payload, JsonOptions));
            return false;
        }
    }

    /// <summary>Removes any active enrollment code without validating it.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _writer.Delete(name: StoreKey);
        }
    }

    private bool TryLoad(out EnrollmentCodePayload? payload)
    {
        payload = null;
        if (!_reader.TryRead(name: StoreKey, value: out var json) || string.IsNullOrWhiteSpace(json))
            return false;

        payload = JsonSerializer.Deserialize<EnrollmentCodePayload>(json, JsonOptions);
        return payload is not null;
    }

    private sealed record EnrollmentCodePayload(
        [property: JsonPropertyName("codeHash")] string CodeHashBase64,
        [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
        [property: JsonPropertyName("failures")] int Failures);
}
