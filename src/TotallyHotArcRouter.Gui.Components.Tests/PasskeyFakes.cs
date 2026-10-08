using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// A controllable <see cref="IPasskeyAdminClient"/> double shared by the passkey store and component tests.
/// Defaults to an enrolled router with one passkey; each RPC records how it was called and can be made to
/// fail with <see cref="Failure"/>.
/// </summary>
internal sealed class FakePasskeyAdminClient : IPasskeyAdminClient
{
    /// <summary>Gets or sets the status returned by <see cref="GetGateStatusAsync"/>.</summary>
    public PasskeyGateStatusInfo Status { get; set; } = new(
        StoreProtected: true,
        Enrolled: true,
        GrantActive: false,
        GrantExpiresAtUtc: null,
        EnrollmentCommandHint: "--mint-passkey-enrollment-code");

    /// <summary>Gets or sets the passkeys returned by <see cref="ListPasskeysAsync"/>.</summary>
    public IReadOnlyList<PasskeyInfo> Passkeys { get; set; } =
    [
        new PasskeyInfo(Id: "cred-1", Name: "Windows Hello", CreatedAtUtc: new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero),
            BackupEligible: true, BackupState: false)
    ];

    /// <summary>Gets or sets the approvals returned by <see cref="ListRecentApprovalsAsync"/>.</summary>
    public IReadOnlyList<PasskeyApprovalInfo> Approvals { get; set; } = [];

    /// <summary>Gets or sets the grant <see cref="FinishContentUnlockAsync"/> issues.</summary>
    public ContentGrantInfo Grant { get; set; } = new("grant-token", DateTimeOffset.UtcNow.AddMinutes(15));

    /// <summary>When set, every RPC fails with it.</summary>
    public GrpcAdminException? Failure { get; set; }

    /// <summary>Gets the enrollment codes passed to <see cref="BeginEnrollmentAsync"/>.</summary>
    public List<string> EnrollmentCodes { get; } = [];

    /// <summary>Gets the credential names passed to <see cref="FinishEnrollmentAsync"/>.</summary>
    public List<string> EnrolledNames { get; } = [];

    /// <summary>Gets the operations passed to <see cref="BeginOneOperationAsync"/>, in order.</summary>
    public List<string> BegunOperations { get; } = [];

    /// <summary>Gets the operations passed to <see cref="FinishOneOperationAsync"/>, in order.</summary>
    public List<string> FinishedOperations { get; } = [];

    /// <summary>Gets the assertion documents passed to the finish RPCs, in order.</summary>
    public List<string> Assertions { get; } = [];

    /// <summary>Gets how many times <see cref="LockContentAsync"/> ran.</summary>
    public int LockCalls { get; private set; }

    /// <inheritdoc/>
    public Task<PasskeyGateStatusInfo> GetGateStatusAsync(CancellationToken cancellationToken = default) =>
        Run(() => Status);

    /// <inheritdoc/>
    public Task<IReadOnlyList<PasskeyInfo>> ListPasskeysAsync(CancellationToken cancellationToken = default) =>
        Run(() => Passkeys);

    /// <inheritdoc/>
    public Task<string> BeginEnrollmentAsync(string enrollmentCode, CancellationToken cancellationToken = default)
    {
        EnrollmentCodes.Add(enrollmentCode);
        return Run(() => "{\"challenge\":\"enroll\"}");
    }

    /// <inheritdoc/>
    public Task<PasskeyInfo> FinishEnrollmentAsync(string attestationJson, string name,
        CancellationToken cancellationToken = default)
    {
        Assertions.Add(attestationJson);
        EnrolledNames.Add(name);
        return Run(() =>
        {
            var created = new PasskeyInfo(Id: "new-cred", Name: name, CreatedAtUtc: DateTimeOffset.UtcNow,
                BackupEligible: false, BackupState: false);
            Passkeys = [.. Passkeys, created];
            Status = Status with { Enrolled = true };
            return created;
        });
    }

    /// <inheritdoc/>
    public Task<string> BeginContentUnlockAsync(CancellationToken cancellationToken = default) =>
        Run(() => "{\"challenge\":\"unlock\"}");

    /// <inheritdoc/>
    public Task<ContentGrantInfo> FinishContentUnlockAsync(string assertionJson,
        CancellationToken cancellationToken = default)
    {
        Assertions.Add(assertionJson);
        return Run(() => Grant);
    }

    /// <inheritdoc/>
    public Task<string> BeginOneOperationAsync(string operation, string parameters,
        CancellationToken cancellationToken = default)
    {
        BegunOperations.Add(operation);
        return Run(() => "{\"challenge\":\"op\"}");
    }

    /// <inheritdoc/>
    public Task<OneOperationAuthorizationInfo> FinishOneOperationAsync(string assertionJson, string operation,
        string parameters, CancellationToken cancellationToken = default)
    {
        Assertions.Add(assertionJson);
        FinishedOperations.Add(operation);
        return Run(() => new OneOperationAuthorizationInfo($"authz-{operation}", DateTimeOffset.UtcNow.AddMinutes(2)));
    }

    /// <inheritdoc/>
    public Task LockContentAsync(CancellationToken cancellationToken = default)
    {
        LockCalls++;
        return Run(() => 0);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PasskeyApprovalInfo>> ListRecentApprovalsAsync(
        CancellationToken cancellationToken = default) => Run(() => Approvals);

    private Task<T> Run<T>(Func<T> result)
    {
        return Failure is not null ? Task.FromException<T>(Failure) : Task.FromResult(result());
    }
}

/// <summary>
/// An <see cref="IWebAuthnCeremony"/> double that answers every ceremony with a fixed document, or fails like a
/// dismissed browser prompt, so tests can drive the stores and dialogs without a browser.
/// </summary>
internal sealed class FakeWebAuthnCeremony : IWebAuthnCeremony
{
    /// <summary>When set, every ceremony fails with it.</summary>
    public WebAuthnCeremonyException? Failure { get; set; }

    /// <summary>Gets the options documents passed to <see cref="CreateCredentialAsync"/>.</summary>
    public List<string> CreateOptions { get; } = [];

    /// <summary>Gets the options documents passed to <see cref="GetAssertionAsync"/>.</summary>
    public List<string> GetOptions { get; } = [];

    /// <inheritdoc/>
    public Task<string> CreateCredentialAsync(string optionsJson, CancellationToken cancellationToken = default)
    {
        CreateOptions.Add(optionsJson);
        return Failure is not null ? Task.FromException<string>(Failure) : Task.FromResult("{\"attestation\":true}");
    }

    /// <inheritdoc/>
    public Task<string> GetAssertionAsync(string optionsJson, CancellationToken cancellationToken = default)
    {
        GetOptions.Add(optionsJson);
        return Failure is not null ? Task.FromException<string>(Failure) : Task.FromResult("{\"assertion\":true}");
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> with a settable clock whose timers fire only when the test says so, so grant
/// expiry is tested without sleeping.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    /// <summary>Initializes a new instance of the <see cref="ManualTimeProvider"/> class at <paramref name="now"/>.</summary>
    public ManualTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    /// <summary>Gets the number of timers created and not yet disposed.</summary>
    public int LiveTimerCount => _timers.Count(t => !t.Disposed);

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock to <paramref name="now"/> without firing any timer, to model a timer that is late.</summary>
    public void SetNowWithoutFiringTimers(DateTimeOffset now)
    {
        _now = now;
    }

    /// <summary>Moves the clock forward by <paramref name="by"/> and fires every live timer that has come due.</summary>
    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var timer in _timers.Where(t => !t.Disposed && t.DueAt <= _now).ToList())
        {
            timer.Disposed = true;
            timer.Callback(timer.State);
        }
    }

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, _now + dueTime);
        _timers.Add(timer);
        return timer;
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset dueAt) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public DateTimeOffset DueAt { get; } = dueAt;

        public bool Disposed { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
