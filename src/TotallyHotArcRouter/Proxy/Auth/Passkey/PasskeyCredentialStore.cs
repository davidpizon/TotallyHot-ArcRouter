using System.Text.Json;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Proxy.Auth.Passkey;

/// <summary>
/// Persists enrolled WebAuthn credentials as a JSON array under <see cref="StoreKey"/> in
/// <see cref="ProtectedSecretStore"/>. A process-wide lock serializes read-modify-write cycles so the
/// router and an elevated enrollment channel cannot interleave writes and lose credentials.
/// </summary>
public sealed class PasskeyCredentialStore : IPasskeyCredentialStore
{
    /// <summary>Secret-store key for the credential JSON array (ADR-0020 §3.4).</summary>
    private const string StoreKey = "passkey.credentials.v1";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ISecretReader _reader;
    private readonly ISecretWriter _writer;
    private readonly Lock _lock = new();

    /// <summary>Initializes a new instance using one store for both read and write.</summary>
    /// <param name="store">The protected secret store backing credentials.</param>
    public PasskeyCredentialStore(ProtectedSecretStore store)
        : this(reader: store, writer: store)
    {
    }

    /// <summary>Initializes a new instance with explicit reader and writer surfaces.</summary>
    /// <param name="reader">Reads the encrypted store.</param>
    /// <param name="writer">Writes the encrypted store.</param>
    private PasskeyCredentialStore(ISecretReader reader, ISecretWriter writer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        _reader = reader;
        _writer = writer;
    }

    /// <inheritdoc/>
    public IReadOnlyList<PasskeyCredentialRecord> List()
    {
        lock (_lock)
        {
            return Load().ToList();
        }
    }

    /// <inheritdoc/>
    public void Add(PasskeyCredentialRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            var list = Load();
            list.Add(record);
            Save(list);
        }
    }

    /// <inheritdoc/>
    public void Update(PasskeyCredentialRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock)
        {
            var list = Load();
            var id = PasskeyEncoding.ToBase64Url(record.Id);
            var index = list.FindIndex(c => PasskeyEncoding.ToBase64Url(c.Id) == id);
            if (index < 0)
                throw new InvalidOperationException("Credential to update was not found.");

            list[index] = record;
            Save(list);
        }
    }

    /// <inheritdoc/>
    public bool Remove(string credentialIdBase64Url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialIdBase64Url);
        lock (_lock)
        {
            var list = Load();
            var removed = list.RemoveAll(c => PasskeyEncoding.ToBase64Url(c.Id) == credentialIdBase64Url);
            if (removed == 0) return false;

            Save(list);
            return true;
        }
    }

    private List<PasskeyCredentialRecord> Load()
    {
        if (!_reader.TryRead(name: StoreKey, value: out var json) || string.IsNullOrWhiteSpace(json))
            return [];

        var dtos = JsonSerializer.Deserialize<List<PasskeyCredentialDto>>(json, JsonOptions);
        if (dtos is null || dtos.Count == 0) return [];

        return dtos.ConvertAll(PasskeyCredentialRecord.FromDto);
    }

    private void Save(IReadOnlyList<PasskeyCredentialRecord> records)
    {
        var dtos = records.Select(r => r.ToDto()).ToList();
        var json = JsonSerializer.Serialize(dtos, JsonOptions);
        _writer.Write(name: StoreKey, value: json);
    }
}
