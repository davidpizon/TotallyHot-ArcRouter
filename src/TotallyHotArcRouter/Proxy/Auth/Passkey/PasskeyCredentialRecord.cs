using System.Text.Json.Serialization;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// One enrolled WebAuthn credential persisted in the protected secret store (ADR-0020). The public key
/// material is not secret, but only privileged writers can change this list; the record travels in memory
/// with raw byte arrays while JSON on disk uses base64url fields via <see cref="PasskeyCredentialDto"/>.
/// </summary>
/// <param name="Id">The credential id bytes returned by the authenticator.</param>
/// <param name="PublicKey">The COSE-encoded public key bytes.</param>
/// <param name="SignCount">The authenticator signature counter from the last successful assertion.</param>
/// <param name="Name">The operator-chosen display name shown in the GUI.</param>
/// <param name="CreatedAtUtc">When the credential was enrolled.</param>
/// <param name="BackupEligible">Whether the authenticator reported backup eligibility (<c>BE</c>).</param>
/// <param name="BackupState">Whether the authenticator reported a backed-up key (<c>BS</c>).</param>
public sealed record PasskeyCredentialRecord(
    byte[] Id,
    byte[] PublicKey,
    uint SignCount,
    string Name,
    DateTimeOffset CreatedAtUtc,
    bool BackupEligible,
    bool BackupState)
{
    /// <summary>Maps this record to its JSON DTO for persistence.</summary>
    public PasskeyCredentialDto ToDto() => new(
        Id: PasskeyEncoding.ToBase64Url(Id),
        PublicKey: PasskeyEncoding.ToBase64Url(PublicKey),
        SignCount: SignCount,
        Name: Name,
        CreatedAtUtc: CreatedAtUtc,
        BackupEligible: BackupEligible,
        BackupState: BackupState);

    /// <summary>Rehydrates a record from a persisted DTO.</summary>
    public static PasskeyCredentialRecord FromDto(PasskeyCredentialDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new PasskeyCredentialRecord(
            Id: PasskeyEncoding.FromBase64Url(dto.Id),
            PublicKey: PasskeyEncoding.FromBase64Url(dto.PublicKey),
            SignCount: dto.SignCount,
            Name: dto.Name,
            CreatedAtUtc: dto.CreatedAtUtc,
            BackupEligible: dto.BackupEligible,
            BackupState: dto.BackupState);
    }
}

/// <summary>
/// JSON shape stored in <see cref="TotallyHot.ArcRouter.Proxy.Management.ProtectedSecretStore"/> under
/// <c>passkey.credentials.v1</c>. Base64url
/// encoding keeps credential ids and COSE keys compact and copy-safe in a UTF-8 JSON file.
/// </summary>
public sealed record PasskeyCredentialDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("signCount")] uint SignCount,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("backupEligible")] bool BackupEligible,
    [property: JsonPropertyName("backupState")] bool BackupState);
