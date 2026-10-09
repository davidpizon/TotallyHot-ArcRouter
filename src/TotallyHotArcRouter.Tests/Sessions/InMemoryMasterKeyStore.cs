using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>A master-key store that keeps both entries in memory, standing in for the secret store.</summary>
internal sealed class InMemoryMasterKeyStore : ISessionMasterKeyStore
{
    private byte[]? _current;
    private byte[]? _next;
    private bool _rotationRequired;

    /// <summary>Gets or sets whether the next <see cref="PromoteNext"/> throws once, as a locked secret store would.</summary>
    public bool FailNextPromote { get; set; }

    /// <summary>Gets or sets whether the next <see cref="StageNext"/> throws once, as a locked secret store would.</summary>
    public bool FailNextStage { get; set; }

    /// <inheritdoc/>
    public byte[] GetOrCreateCurrent() => (byte[])(_current ??= SessionKeyMaterial.CreateMasterKey()).Clone();

    /// <inheritdoc/>
    public byte[]? TryGetCurrent() => (byte[]?)_current?.Clone();

    /// <inheritdoc/>
    public byte[]? TryGetNext() => (byte[]?)_next?.Clone();

    /// <inheritdoc/>
    public void StageNext(byte[] nextKey)
    {
        if (FailNextStage)
        {
            FailNextStage = false;
            throw new IOException("The secret store is locked.");
        }

        _next = (byte[])nextKey.Clone();
    }

    /// <inheritdoc/>
    public void PromoteNext()
    {
        if (FailNextPromote)
        {
            FailNextPromote = false;
            throw new IOException("The secret store is locked.");
        }

        if (_next is null) return;
        _current = _next;
        _next = null;
    }

    /// <inheritdoc/>
    public void DiscardNext() => _next = null;

    /// <inheritdoc/>
    public void RequireRotation() => _rotationRequired = true;

    /// <inheritdoc/>
    public bool IsRotationRequired() => _rotationRequired;

    /// <inheritdoc/>
    public void ClearRotationRequired() => _rotationRequired = false;

    /// <inheritdoc/>
    public void DestroyAll()
    {
        _current = null;
        _next = null;
        _rotationRequired = false;
    }
}
