using Microsoft.AspNetCore.Components;
using TotallyHot.ArcRouter.Gui.Services;
using TotallyHot.ArcRouter.Gui.Telemetry;

namespace TotallyHot.ArcRouter.Gui.Components;

/// <summary>
/// System settings modal: telemetry connection settings, the adaptive-routing toggle and sample size
/// (docs/router/self-organizing-classification-plan.md Phase T6), the shadow-judge toggle and backbone
/// picker (docs/router/geval-shadow-scoring-plan.md), the transcription-capture toggle and its Clear action
/// (docs/router/self-organizing-classification-plan.md Phase T1), destructive actions gated behind a typed
/// confirmation word, and a discreet GUI/Router version footer.
/// </summary>
public partial class SettingsModal
{
    /// <summary>
    /// The inclusive lower bound accepted for Sample Size, mirroring
    /// <c>RouterSettingsAdminGrpcService.MinEmbeddingMemoryCapacity</c>.
    /// </summary>
    private const int MinEmbeddingMemoryCapacity = 500;

    /// <summary>
    /// The inclusive upper bound accepted for Sample Size, mirroring
    /// <c>RouterSettingsAdminGrpcService.MaxEmbeddingMemoryCapacity</c>.
    /// </summary>
    private const int MaxEmbeddingMemoryCapacity = 50_000;

    /// <summary>
    /// The Sample Size below which the warning icon/tooltip renders, matching <c>RoutingOptions</c>'s shipped
    /// default.
    /// </summary>
    private const int RecommendedEmbeddingMemoryCapacity = 20_000;

    private string? _activeAction;
    private bool _adaptiveRoutingEnabled;
    private bool _clearTranscriptsFailed;
    private string? _clearTranscriptsMessage;
    private bool _clearingTranscripts;
    private ElementReference _confirmInput;
    private string _confirmText = string.Empty;
    private bool _confirmingApply;
    private bool _confirmingClearTranscripts;
    private IReadOnlyList<string> _eligibleJudgeModels = [];
    private int _embeddingMemoryCapacity = RecommendedEmbeddingMemoryCapacity;
    private bool _focusPending;
    private bool _judgeEnabled;
    private string _judgeModelName = string.Empty;
    private string _persistedTelemetryAddress = string.Empty;
    private string? _routerSettingsMessage;
    private bool _routerSettingsSaveFailed;
    private bool _routerSettingsSaving;
    private string _sampleSizeText = RecommendedEmbeddingMemoryCapacity.ToString();
    private string _telemetryAddress = string.Empty;
    private bool _telemetryAddressSaved;
    private bool _transcriptCaptureEnabled;
    private string? _updateErrorMessage;

    /// <summary>Invoked when the modal should close - clicking the backdrop, the X button, or confirming a destructive action.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>The exact word the operator must type to confirm the active destructive action.</summary>
    private string Required => _activeAction == "reset" ? "RESET" : "PURGE";

    /// <summary>Whether the typed confirmation text exactly matches <see cref="Required"/>.</summary>
    private bool IsConfirmed => _confirmText == Required;

    /// <summary>Whether the telemetry address field differs from the last-persisted value.</summary>
    private bool HasTelemetryAddressChanged => _telemetryAddress != _persistedTelemetryAddress;

    /// <summary>Whether the telemetry address field holds a real, savable pending change.</summary>
    private bool CanSaveTelemetryAddress =>
        HasTelemetryAddressChanged && !string.IsNullOrWhiteSpace(_telemetryAddress.Trim());

    // Read off the live (possibly out-of-bounds or unparsable) text rather than the clamped
    // _embeddingMemoryCapacity, which only updates on blur/save - otherwise the warning would lag a full
    // keystroke behind what the operator actually typed.
    /// <summary>Whether the warning icon/tooltip should render for the Sample Size text as currently typed.</summary>
    private bool ShowSampleSizeWarning => int.TryParse(s: _sampleSizeText, result: out var parsed) &&
                                          parsed < RecommendedEmbeddingMemoryCapacity;

    /// <summary>
    /// The footer's Router half: the version reported by the Router that answered the most recent status
    /// poll, or "Router unknown" when none did.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="Services.UpdateStore.IsReachable"/> rather than merely on a non-null Status, because
    /// <see cref="UpdateStore"/> is an app-lifetime singleton that keeps its last successful status when
    /// a later poll fails. Reading Status alone would therefore keep displaying the version of a Router
    /// that has since stopped, which is precisely the case this label exists to make visible. A blank
    /// CurrentVersion is treated as unknown too - a Router that answered without naming a version tells
    /// us no more than one that did not answer.
    /// </remarks>
    private string RouterVersionLabel =>
        UpdateStore is { IsReachable: true, Status.CurrentVersion: { } version } && !string.IsNullOrWhiteSpace(version)
            ? $"Router v{version}"
            : "Router unknown";

    /// <inheritdoc/>
    public void Dispose()
    {
        UpdateStore.Changed -= OnUpdateStoreChanged;
    }

    /// <inheritdoc/>
    protected override async Task OnInitializedAsync()
    {
        var loadedAddress = SettingsStore.Load().TelemetryServerAddress;
        _telemetryAddress = loadedAddress;
        _persistedTelemetryAddress = loadedAddress;

        UpdateStore.Changed += OnUpdateStoreChanged;
        await UpdateStore.LoadAsync();

        await RouterSettingsStore.LoadAsync();
        if (RouterSettingsStore.Settings is { } settings) ApplyRouterSettings(settings);
    }

    // Shared by the initial load and every instant save's post-mutation refresh (success re-syncs to the
    // server's canonical values; failure rolls back to the last-known-good ones), so both paths treat
    // RouterSettingsAdminStore.Settings as the single source of truth the same way.
    /// <summary>Copies <paramref name="settings"/> into the form fields.</summary>
    private void ApplyRouterSettings(RouterSettingsInfo settings)
    {
        _adaptiveRoutingEnabled = settings.AdaptiveRoutingEnabled;
        _embeddingMemoryCapacity = settings.EmbeddingMemoryCapacity;
        _sampleSizeText = settings.EmbeddingMemoryCapacity.ToString();
        _judgeEnabled = settings.JudgeEnabled;
        _eligibleJudgeModels = settings.EligibleJudgeModels;
        _transcriptCaptureEnabled = settings.TranscriptCaptureEnabled;

        // A stored pick that is no longer eligible is shown as Automatic rather than as a phantom
        // option: that is what the selector will actually do at call time, and re-offering the dead
        // name would let the operator "save" a choice the server would now reject.
        _judgeModelName =
            _eligibleJudgeModels.Contains(value: settings.JudgeModelName, comparer: StringComparer.OrdinalIgnoreCase)
                ? settings.JudgeModelName
                : string.Empty;
    }

    /// <summary>Updates the address field and clears any stale "Saved" confirmation from a previous save.</summary>
    private void OnTelemetryAddressInput(ChangeEventArgs e)
    {
        _telemetryAddress = (string?)e.Value ?? string.Empty;
        _telemetryAddressSaved = false;
    }

    /// <summary>Persists the telemetry address once the operator explicitly confirms it via the Save button.</summary>
    private void SaveTelemetryAddress()
    {
        var trimmed = _telemetryAddress.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;

        _telemetryAddress = trimmed;
        _persistedTelemetryAddress = trimmed;
        SettingsStore.Save(SettingsStore.Load() with { TelemetryServerAddress = trimmed });
        _telemetryAddressSaved = true;
    }

    /// <summary>Discards the in-progress edit, restoring the field to the last-persisted address.</summary>
    private void UndoTelemetryAddress()
    {
        _telemetryAddress = _persistedTelemetryAddress;
        _telemetryAddressSaved = false;
    }

    /// <summary>Flips the adaptive-routing toggle and saves it immediately.</summary>
    private Task ToggleAdaptiveRouting()
    {
        _adaptiveRoutingEnabled = !_adaptiveRoutingEnabled;
        return SaveRouterSettingsNow();
    }

    /// <summary>Flips the shadow-judge toggle and saves it immediately.</summary>
    private Task ToggleJudgeEnabled()
    {
        _judgeEnabled = !_judgeEnabled;
        return SaveRouterSettingsNow();
    }

    /// <summary>Tracks the judge-model selection (an empty value is the explicit "Automatic" choice) and saves it immediately.</summary>
    private Task OnJudgeModelChange(ChangeEventArgs e)
    {
        _judgeModelName = (string?)e.Value ?? string.Empty;
        return SaveRouterSettingsNow();
    }

    /// <summary>Flips the transcription-capture toggle and saves it immediately.</summary>
    private Task ToggleTranscriptCapture()
    {
        _transcriptCaptureEnabled = !_transcriptCaptureEnabled;
        return SaveRouterSettingsNow();
    }

    /// <summary>Opens the Clear confirmation and clears any stale outcome from a previous clear.</summary>
    private void StartClearTranscripts()
    {
        _confirmingClearTranscripts = true;
        _clearTranscriptsMessage = null;
        _clearTranscriptsFailed = false;
    }

    /// <summary>Runs the confirmed Clear action, deleting every captured transcript row.</summary>
    private async Task ClearTranscriptsConfirmed()
    {
        _clearingTranscripts = true;
        try
        {
            var rowsDeleted = await RouterSettingsStore.ClearTranscriptsAsync();
            _clearTranscriptsMessage = rowsDeleted == 1 ? "Cleared 1 row." : $"Cleared {rowsDeleted} rows.";
            _clearTranscriptsFailed = false;
        }
        catch (RouterSettingsAdminException)
        {
            _clearTranscriptsMessage = RouterSettingsStore.IsReachable
                ? RouterSettingsStore.LastError
                : "Could not reach the router. Is the proxy running?";
            _clearTranscriptsFailed = true;
        }
        finally
        {
            _clearingTranscripts = false;
            _confirmingClearTranscripts = false;
        }
    }

    // Only tracks the raw text here - deliberately does NOT parse into _embeddingMemoryCapacity on every
    // keystroke. The input's value is bound to _sampleSizeText (not the int), so an in-progress edit that
    // doesn't currently parse (e.g. the field cleared to type a new number) is preserved verbatim instead of
    // being clobbered by a stale re-render of the last-known-good integer.
    /// <summary>Tracks the Sample Size input's raw text as the operator types, and clears any stale save outcome.</summary>
    private void OnSampleSizeInput(ChangeEventArgs e)
    {
        _sampleSizeText = (string?)e.Value ?? string.Empty;
        ClearRouterSettingsMessage();
    }

    /// <summary>Parses and clamps the Sample Size once the operator leaves the field, then saves it immediately.</summary>
    private Task OnSampleSizeChange(ChangeEventArgs e)
    {
        _sampleSizeText = (string?)e.Value ?? string.Empty;
        ClampSampleSize();
        return SaveRouterSettingsNow();
    }

    /// <summary>
    /// Parses <see cref="_sampleSizeText"/> into <see cref="_embeddingMemoryCapacity"/> (keeping the prior
    /// value on a parse failure), clamps into <see cref="MinEmbeddingMemoryCapacity"/>/
    /// <see cref="MaxEmbeddingMemoryCapacity"/> - defense in depth ahead of the server-side clamp
    /// <c>RouterSettingsAdminGrpcService.UpdateRouterSettings</c> actually enforces - and resyncs the
    /// display text to the clamped canonical value.
    /// </summary>
    private void ClampSampleSize()
    {
        if (int.TryParse(s: _sampleSizeText, result: out var parsed)) _embeddingMemoryCapacity = parsed;

        _embeddingMemoryCapacity = Math.Clamp(value: _embeddingMemoryCapacity, min: MinEmbeddingMemoryCapacity,
            max: MaxEmbeddingMemoryCapacity);
        _sampleSizeText = _embeddingMemoryCapacity.ToString();
    }

    /// <summary>
    /// Clears the router settings' save outcome message, e.g. because the operator edited a field after a prior save
    /// attempt.
    /// </summary>
    private void ClearRouterSettingsMessage()
    {
        _routerSettingsMessage = null;
        _routerSettingsSaveFailed = false;
    }

    // Called from every Adaptive Routing / Shadow Judge control's own change handler - there is no footer
    // Save button for this section, so each edit must reach the router on its own. The underlying RPC
    // (UpdateRouterSettingsAsync) always writes all four fields together; that is fine here because every
    // caller already holds the form's full current state, not just the field that just changed.
    //
    // Reads the outcome off RouterSettingsAdminStore's own IsReachable/LastError rather than recomputing a
    // message from the caught exception - the store already resolved unreachable-vs-rejected via
    // IRouterSettingsAdminClient's isUnavailable flag, so recomputing it here would just duplicate that logic.
    /// <summary>
    /// Persists the adaptive-routing and shadow-judge settings through <see cref="RouterSettingsAdminStore"/>
    /// immediately.
    /// </summary>
    private async Task SaveRouterSettingsNow()
    {
        _routerSettingsSaving = true;
        try
        {
            await RouterSettingsStore.UpdateAsync(
                adaptiveRoutingEnabled: _adaptiveRoutingEnabled,
                embeddingMemoryCapacity: _embeddingMemoryCapacity,
                judgeEnabled: _judgeEnabled,
                judgeModelName: _judgeModelName,
                transcriptCaptureEnabled: _transcriptCaptureEnabled);
            _routerSettingsMessage = "Saved";
            _routerSettingsSaveFailed = false;

            // The server is now the single source of truth for what actually took effect (it recomputes the
            // eligible judge-model list too), so resync the whole form to its response rather than only the
            // list, the way the pre-instant-save footer button used to.
            if (RouterSettingsStore.Settings is { } saved) ApplyRouterSettings(saved);
        }
        catch (RouterSettingsAdminException)
        {
            _routerSettingsMessage = RouterSettingsStore.IsReachable
                ? RouterSettingsStore.LastError
                : "Could not reach the router. Is the proxy running?";
            _routerSettingsSaveFailed = true;

            // The edit never took effect, so roll the form back to whatever last actually saved - otherwise
            // the toggle would keep showing a state the router never received.
            if (RouterSettingsStore.Settings is { } lastGood) ApplyRouterSettings(lastGood);
        }
        finally
        {
            _routerSettingsSaving = false;
        }
    }

    /// <summary>Opens the type-to-confirm step for the given destructive action and queues focus on its input.</summary>
    private void StartAction(string action)
    {
        _activeAction = action;
        _confirmText = string.Empty;
        _focusPending = true;
    }

    /// <summary>Closes the type-to-confirm step without executing the action.</summary>
    private void CancelAction()
    {
        _activeAction = null;
        _confirmText = string.Empty;
    }

    // Both actions clear this session's live view (LiveDataStore.ClearEvents); Clear History additionally
    // empties the Console tab's log buffer, since "history" reads as everything accumulated, not just the
    // Live Stream/Cost Analytics stats "Reset Stats" implies. Neither touches the proxy's own durable
    // history (see ClearEvents' remarks) or any persisted configuration - the note below the buttons stays
    // accurate.
    /// <summary>Runs the confirmed destructive action (Reset Stats or Clear History), then closes the modal.</summary>
    private async Task ExecuteAction()
    {
        if (!IsConfirmed) return;

        LiveDataStore.ClearEvents();
        if (_activeAction == "purge") LiveDataStore.ClearLogLines();

        CancelAction();
        await OnClose.InvokeAsync();
    }

    /// <inheritdoc/>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusPending)
        {
            _focusPending = false;
            await _confirmInput.FocusAsync();
        }
    }

    /// <summary>Re-renders when <see cref="UpdateStore"/> publishes a state change (a load, check, or apply completing).</summary>
    private void OnUpdateStoreChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    /// <summary>Forces an immediate update check via the "Check Now" button.</summary>
    private async Task CheckForUpdatesNow()
    {
        _updateErrorMessage = null;
        try
        {
            await UpdateStore.CheckNowAsync();
        }
        catch (UpdateAdminException)
        {
            // UpdateStore already recorded IsReachable/LastError; the panel reads those via
            // UpdateStore.Status remaining unchanged on failure, so nothing further to do here.
        }
    }

    /// <summary>Opens the "this restarts the Router service" confirmation ahead of applying.</summary>
    private void StartApplyConfirmation()
    {
        _confirmingApply = true;
        _updateErrorMessage = null;
    }

    /// <summary>Applies the update once the operator has confirmed the install/restart.</summary>
    private async Task ApplyUpdateConfirmed()
    {
        _confirmingApply = false;
        try
        {
            await UpdateStore.ApplyAsync();
        }
        catch (InvalidOperationException ex)
        {
            // No update was known available - e.g. a background CheckNowAsync/LoadAsync raced this
            // confirmation and cleared the status between the button appearing and the click landing.
            _updateErrorMessage = ex.Message;
        }
    }
}