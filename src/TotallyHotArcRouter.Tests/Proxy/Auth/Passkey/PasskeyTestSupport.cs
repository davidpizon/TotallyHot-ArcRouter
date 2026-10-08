using TotallyHot.ArcRouter.Proxy.Auth.Passkey;

namespace TotallyHot.ArcRouter.Tests.Proxy.Auth.Passkey;

internal sealed class InMemoryPasskeyCredentialStore : IPasskeyCredentialStore
{
    private readonly List<PasskeyCredentialRecord> _records = [];

    public IReadOnlyList<PasskeyCredentialRecord> List() => _records.ToList();

    public void Add(PasskeyCredentialRecord record) => _records.Add(record);

    public void Update(PasskeyCredentialRecord record)
    {
        var id = PasskeyEncoding.ToBase64Url(record.Id);
        var index = _records.FindIndex(r => PasskeyEncoding.ToBase64Url(r.Id) == id);
        if (index < 0) throw new InvalidOperationException("Not found.");
        _records[index] = record;
    }

    public bool Remove(string credentialIdBase64Url) =>
        _records.RemoveAll(r => PasskeyEncoding.ToBase64Url(r.Id) == credentialIdBase64Url) > 0;
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
