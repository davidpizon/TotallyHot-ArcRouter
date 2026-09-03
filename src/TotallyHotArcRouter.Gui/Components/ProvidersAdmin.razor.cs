using System.Globalization;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Charts;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Components;

/// <summary>
/// Governance &gt; Providers pane: add/remove/edit provider endpoints + credentials and manage each
/// provider's models (with live <c>/v1/models</c> discovery). Talks to the proxy's <c>/admin</c> API via
/// the injected <see cref="ProviderAdminStore"/>.
/// </summary>
public partial class ProvidersAdmin
{
    // Cycled by model index rather than picked per model name, so the palette stays stable regardless of
    // how many distinct models a provider reports on a given day.
    private static readonly string[] ReportedUsageSeriesColors =
        ["#1ed760", "#10b981", "#38bdf8", "#a78bfa", "#f59e0b", "#f472b6"];

    // Which providers currently have the "Add model manually" pane expanded - absent/false is the default
    // (collapsed), matching every other per-provider draft dictionary's "missing key = default" shape.
    private readonly HashSet<string> _addModelPaneOpen = [];
    private readonly Dictionary<string, string> _draftAdminKey = [];
    private readonly Dictionary<string, string> _draftDollarCap = [];
    private readonly Dictionary<string, string> _draftId = [];
    private readonly Dictionary<string, string> _draftName = [];
    private readonly Dictionary<string, string> _draftTokenCap = [];

    // Providers whose rate-limit trend history has already been requested this session - loaded once per
    // provider rather than on every Store.Changed re-render, since the history endpoint is a separate
    // request from the provider list itself.
    private readonly HashSet<string> _historyRequested = new(StringComparer.OrdinalIgnoreCase);
    private string? _dialogError;

    private ProviderEditDialog.ProviderEditModel _dialogModel = new(
        Key: string.Empty,
        false,
        BaseUrl: string.Empty,
        AuthHeaderName: "Authorization",
        Headers: [],
        false,
        ProviderType: "Other",
        ProviderName: string.Empty);

    private string? _opError;

    // Non-null while the type-to-confirm removal dialog is open, holding the provider it targets.
    private string? _removeKey;
    private int _removeModelCount;

    private bool _showDialog;

    /// <summary>The dialects the override dropdown offers, plus the "auto-detect" entry the markup adds.</summary>
    private static IReadOnlyList<string> ToolDialects => ToolCallDialectNames.All;

    /// <summary>Unsubscribes from <see cref="ProviderAdminStore.Changed"/>.</summary>
    public void Dispose()
    {
        Store.Changed -= OnStoreChanged;
    }

    /// <inheritdoc/>
    protected override async Task OnInitializedAsync()
    {
        Store.Changed += OnStoreChanged;
        await Store.LoadAsync();
        RequestRateLimitHistories();
    }

    // Store.Changed is raised from async continuations in ProviderAdminStore, not guaranteed to already be
    // on the Blazor renderer's sync context - RequestRateLimitHistories mutates _historyRequested (a plain,
    // non-thread-safe HashSet), so it has to run inside the same InvokeAsync as StateHasChanged rather than
    // directly on whatever thread raised the event.
    /// <summary>Re-requests rate-limit history for any newly-eligible providers and re-renders when the store's state changes.</summary>
    private void OnStoreChanged()
    {
        InvokeAsync(() =>
        {
            RequestRateLimitHistories();
            StateHasChanged();
        });
    }

    // Kicks off (fire-and-forget, best-effort) a history load for every provider that has a rate-limit
    // snapshot and hasn't been requested yet. LoadRateLimitHistoryAsync raises Store.Changed on success,
    // which re-enters this method - the HashSet guard is what stops that from looping.
    /// <summary>
    /// Starts a fire-and-forget rate-limit history load for every provider that has a snapshot and hasn't already
    /// been requested this session.
    /// </summary>
    private void RequestRateLimitHistories()
    {
        foreach (var provider in Store.Providers)
        {
            if (provider.RateLimit is null || !_historyRequested.Add(provider.Key)) continue;

            _ = Store.LoadRateLimitHistoryAsync(provider.Key);
        }
    }

    /// <summary>Clears the inline error and reloads providers. Implements the unreachable state's Retry button.</summary>
    private async Task Reload()
    {
        _opError = null;
        await Store.LoadAsync();
    }

    /// <summary>Opens the edit dialog seeded with blank/default fields for adding a new provider.</summary>
    private void OpenAdd()
    {
        _dialogModel = new ProviderEditDialog.ProviderEditModel(
            Key: string.Empty,
            true,
            BaseUrl: string.Empty,
            AuthHeaderName: "Authorization",
            Headers: [],
            false,
            ProviderType: "Other",
            ProviderName: string.Empty);
        _dialogError = null;
        _showDialog = true;
    }

    /// <summary>Opens the edit dialog seeded with an existing provider's current fields.</summary>
    private void OpenEdit(ProviderAdminView provider)
    {
        _dialogModel = new ProviderEditDialog.ProviderEditModel(
            Key: provider.Key,
            false,
            BaseUrl: provider.BaseUrl,
            AuthHeaderName: provider.AuthHeaderName,
            Headers: provider.Headers,
            IsFree: provider.IsFree,
            // A provider stored before ProviderType existed has none; the dialog falls back to "Other"
            // itself, but pass through what is stored so an Anthropic provider reopens as Anthropic rather
            // than silently reverting to Other on every edit.
            ProviderType: provider.ProviderType ?? "Other",
            ProviderName: provider.Name ?? string.Empty);
        _dialogError = null;
        _showDialog = true;
    }

    /// <summary>Closes the edit dialog without saving.</summary>
    private void CancelDialog()
    {
        _showDialog = false;
        _dialogError = null;
    }

    /// <summary>
    /// Submits the edit dialog's result as an add or edit, closing the dialog on success or showing the rejection
    /// inline.
    /// </summary>
    private async Task SaveDialog(ProviderEditDialog.ProviderEditResult result)
    {
        var body = new ProviderWriteRequest(
            BaseUrl: result.BaseUrl,
            AuthHeaderName: result.AuthHeaderName,
            Headers: result.Headers,
            IsFree: result.IsFree,
            ProviderName: result.ProviderName,
            ProviderType: result.ProviderType);

        try
        {
            await Store.UpsertProviderAsync(key: result.Key, body: body);
            _showDialog = false;
            _dialogError = null;
        }
        catch (ProviderAdminException ex)
        {
            _dialogError = ex.Message;
        }
    }

    // Opens the type-to-confirm dialog rather than removing immediately: this is the sole delete entry
    // point and removal cascades to the provider's models, so the dialog states that consequence and
    // makes the user type the key (RemoveProviderDialog).
    private void RemoveProvider(ProviderAdminView provider)
    {
        _removeKey = provider.Key;
        _removeModelCount = provider.Models.Count;
    }

    /// <summary>Closes the type-to-confirm removal dialog without removing anything.</summary>
    private void CancelRemove()
    {
        _removeKey = null;
        _removeModelCount = 0;
    }

    /// <summary>Removes the provider the removal dialog targeted, after closing the dialog first.</summary>
    private async Task ConfirmRemove()
    {
        var key = _removeKey;
        if (key is null) return;

        // Close first so the dialog doesn't sit over the pane while the request is in flight, and so a
        // server-side rejection lands in the pane's error banner where every other failed operation does.
        CancelRemove();
        await RunAsync(() => Store.RemoveProviderAsync(key));
    }

    // Flips the provider's Enabled flag through the dedicated /enabled route, which preserves every other
    // configured field. The proxy's routing path (RequestInterceptor/ProxyMiddleware) skips a stopped
    // provider, so this takes effect on the next request.
    private async Task ToggleEnabled(ProviderAdminView provider)
    {
        await RunAsync(() =>
            Store.SetEnabledAsync(key: provider.Key, body: new ProviderEnabledWriteRequest(!provider.Enabled)));
    }

    // The model-level twin of ToggleEnabled: flips a model's own Start/Stop state through the dedicated
    // /enabled route, independent of whether the last scan reported it present.
    /// <summary>Flips one model's own Start/Stop state.</summary>
    private async Task ToggleModelEnabled(ProviderAdminView provider, ModelAdminView model)
    {
        await RunAsync(() => Store.SetModelEnabledAsync(key: provider.Key, modelName: model.ModelName,
            body: new ModelEnabledWriteRequest(!model.Enabled)));
    }

    /// <summary>Removes one model from a provider.</summary>
    private async Task RemoveModel(string key, string modelName)
    {
        await RunAsync(() => Store.RemoveModelAsync(key: key, modelName: modelName));
    }

    // Pins how this model expresses tool calls, or - for an empty selection - clears the pin so automatic
    // detection resumes. Worth an operator control because automatic detection can reach a wrong answer that
    // never self-corrects: a model that emits native tool_calls on only some replies is recorded
    // openai-native on the first one that succeeds, and is then never re-inspected.
    /// <summary>Pins (or clears, for an empty selection) a model's tool-call dialect override.</summary>
    private async Task SetModelDialect(string key, string modelName, string? dialect)
    {
        await RunAsync(() =>
            Store.SetModelToolDialectAsync(key: key, modelName: modelName,
                body: new ModelToolDialectWriteRequest(dialect)));
    }

    /// <summary>Whether a provider's "Add model manually" pane is currently expanded.</summary>
    private bool IsAddModelPaneOpen(string key)
    {
        return _addModelPaneOpen.Contains(key);
    }

    /// <summary>Expands or collapses a provider's "Add model manually" pane.</summary>
    private void ToggleAddModelPane(string key)
    {
        if (!_addModelPaneOpen.Remove(key)) _addModelPaneOpen.Add(key);
    }

    /// <summary>Submits the manual add-model form's draft name/id, clearing the drafts on success.</summary>
    private async Task AddModel(string key)
    {
        var name = GetDraft(drafts: _draftName, key: key).Trim();
        if (string.IsNullOrEmpty(name)) return;

        var id = GetDraft(drafts: _draftId, key: key).Trim();
        var body = new ModelWriteRequest(string.IsNullOrEmpty(id) ? null : id);
        if (await RunAsync(() => Store.UpsertModelAsync(key: key, modelName: name, body: body)))
        {
            _draftName[key] = string.Empty;
            _draftId[key] = string.Empty;
        }
    }

    // The router does all the work here (discovery, model-list reconciliation, endpoint-flavor scan,
    // dialect detection - see ManagementFacade.RefreshFromEndpointAsync); this just triggers it and
    // publishes the fresh result, same as every other mutation on this page.
    /// <summary>
    /// Triggers a live re-scan of a provider's endpoint: model discovery/reconciliation, API-flavor detection, and
    /// dialect detection.
    /// </summary>
    private async Task RefreshFromEndpoint(string key)
    {
        await RunAsync(() => Store.RefreshFromEndpointAsync(key));
    }

    /// <summary>The warning icon's tooltip text for a provider whose last admin interaction failed.</summary>
    private static string AdminActionTip(ProviderInteractionStatusAdminView failure)
    {
        return $"{failure.Operation} failed {FormatUtc(failure.AtUtc)}: {failure.Message}";
    }

    /// <summary>
    /// The warning icon's tooltip text for a provider whose live traffic last failed - worded per
    /// <see cref="ProviderInteractionStatusAdminView.Kind"/> so an out-of-credits trip reads distinctly
    /// from a generic live-traffic failure (docs/adr/0004-surface-out-of-credits-provider-failures-on-
    /// the-providers-tab.md).
    /// </summary>
    private static string LiveTrafficTip(ProviderInteractionStatusAdminView failure)
    {
        return failure.Kind == ProviderInteractionKindAdminView.OutOfCredits
            ? $"Out of credits {FormatUtc(failure.AtUtc)}: {failure.Message}"
            : $"{failure.Operation} failed {FormatUtc(failure.AtUtc)}: {failure.Message}";
    }

    /// <summary>The API-flavor badge labels a provider's last capability scan detected, if any.</summary>
    private static IEnumerable<string> DetectedApis(ProviderEndpointCapabilitiesView? capabilities)
    {
        if (capabilities is null) yield break;

        if (capabilities.AnthropicCompatible) yield return "Anthropic";

        if (capabilities.OpenAiCompatible) yield return "OpenAI";

        if (capabilities.LmStudioNative) yield return "LM Studio";

        if (capabilities.OllamaNative) yield return "Ollama";
    }

    /// <summary>
    /// Runs a mutation, surfacing a failure in the inline error banner rather than letting it escape into the
    /// renderer.
    /// </summary>
    private async Task<bool> RunAsync(Func<Task> operation)
    {
        _opError = null;
        try
        {
            await operation();
            return true;
        }
        catch (ProviderAdminException ex)
        {
            _opError = ex.Message;
            return false;
        }
    }

    /// <summary>The in-progress draft text for a per-provider input, or empty if none has been typed yet.</summary>
    private static string GetDraft(Dictionary<string, string> drafts, string key)
    {
        return drafts.TryGetValue(key: key, value: out var value) ? value : string.Empty;
    }

    /// <summary>Coerces a Blazor event value to a non-null string.</summary>
    private static string AsString(object? value)
    {
        return (string?)value ?? string.Empty;
    }

    // A budget input shows the user's in-progress edit if there is one, otherwise the persisted value (so a
    // freshly loaded card reflects what's stored). A successful save clears the draft, letting the input
    // re-seed from the refreshed provider.
    /// <summary>The in-progress budget draft for a provider, or the persisted cap when there is no unsaved edit.</summary>
    private static string GetBudgetDraft(Dictionary<string, string> drafts, string key, string? current)
    {
        return drafts.TryGetValue(key: key, value: out var value) ? value : (current ?? string.Empty);
    }

    // Percentage of a cap consumed, clamped to 0-100 for the bar height. A zero cap reads as 100% (fully
    // breached) to match the backend's spend >= cap rule - a "$0/month" provider is over budget the moment
    // it exists, so the bar must show CRITICAL, not an OK-looking 0%.
    /// <summary>The percentage of a dollar cap consumed, clamped to [0, 100].</summary>
    private static double UtilizationPercent(decimal spent, decimal cap)
    {
        return cap <= 0m ? 100d : (double)Math.Min(val1: spent / cap * 100m, 100m);
    }

    /// <summary>The percentage of a token cap consumed, clamped to [0, 100].</summary>
    private static double UtilizationPercent(long used, long cap)
    {
        return cap <= 0 ? 100d : Math.Min(val1: (double)used / cap * 100d, 100d);
    }

    /// <summary>Formats a decimal amount with two fixed decimal places.</summary>
    private static string Money2(decimal value)
    {
        return value.ToString(format: "F2", provider: CultureInfo.InvariantCulture);
    }

    // OK / approaching / breached, matching the old Budgets view's 80% and 100% thresholds.
    /// <summary>The utilization bar's status color: green under 80%, amber under 100%, red at or above.</summary>
    private static string BarColor(double pct)
    {
        return pct >= 100d ? "#ef4444" : pct >= 80d ? "#f59e0b" : "#10b981";
    }

    // A single-bar GroupedBars chart (0-100 y-axis) for one utilization dimension, colored by status. The
    // renderer ignores the model title, so the human-readable label lives in the surrounding markup.
    /// <summary>Serializes one budget-utilization bar's chart-model JSON.</summary>
    private static string BudgetBarJson(string label, double pct)
    {
        var value = (decimal)Math.Round(value: pct, 1);
        var model = new GroupedBarsModel(
            title: label,
            categories: [label],
            100m,
            series: [new DistributionSeries(Name: "Utilized", Color: BarColor(pct), Data: [value])]);
        return ChartJson.Serialize(model);
    }

    // Backend-stored timestamps only, never the GUI clock (docs/router/anthropic-reported-usage-plan.md
    // Phase 3 test contract) - both footers below format a value the server already computed.
    /// <summary>Formats a UTC timestamp for display.</summary>
    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToString(format: "yyyy-MM-dd HH:mm 'UTC'", formatProvider: CultureInfo.InvariantCulture);
    }

    /// <summary>The estimated-usage footer's "last recorded" caption, or a placeholder when no usage has been recorded.</summary>
    private static string FormatLastRecorded(DateTimeOffset? lastRecordedAtUtc)
    {
        return lastRecordedAtUtc is { } value ? $"Last recorded {FormatUtc(value)}" : "No usage recorded yet";
    }

    // "requests" -> "Requests", "input-tokens" -> "Input tokens" - readable labels for the standard
    // rate-limit family's dimension keys, which RateLimitSnapshotParser leaves as their raw header-segment
    // names since interpretation is a display concern, not a parsing one.
    /// <summary>Converts a raw rate-limit dimension key into a readable label.</summary>
    private static string DimensionLabel(string dimensionName)
    {
        return dimensionName[..1].ToUpperInvariant() + dimensionName[1..].Replace('-', ' ');
    }

    /// <summary>Formats one rate-limit dimension's remaining/limit/reset line.</summary>
    private static string FormatDimension(string dimensionName, RateLimitDimensionAdminView dimension)
    {
        var remaining = dimension.Remaining?.ToString(format: "N0", provider: CultureInfo.InvariantCulture) ?? "?";
        var limit = dimension.Limit?.ToString(format: "N0", provider: CultureInfo.InvariantCulture) ?? "?";
        var reset = dimension.ResetAt is { } resetAt ? $" · resets {FormatUtc(resetAt)}" : string.Empty;
        return $"{DimensionLabel(dimensionName)}: {remaining} of {limit} remaining{reset}";
    }

    /// <summary>Formats one rate-limit window's status/reset line.</summary>
    private static string FormatWindow(string windowName, RateLimitWindowAdminView window)
    {
        var status = window.Status ?? "unknown";
        var reset = window.ResetAt is { } resetAt ? $" · resets {FormatUtc(resetAt)}" : string.Empty;
        return $"{windowName} window: {status}{reset}";
    }

    // "~19 min at current rate" / "~2h 5m at current rate" - no fabricated urgency: this only renders when
    // ManagementFacade has already decided a projection exists (RateLimitProjection.Project returned
    // non-null), so there is nothing to show when burn rate can't be trusted (flat, refilled, reset-before-empty).
    /// <summary>Formats a rate-limit exhaustion projection's estimated time-to-exhaustion caption.</summary>
    private static string FormatBurnRate(RateLimitExhaustionAdminView projection)
    {
        var eta = projection.TimeToExhaustion;
        var span = eta.TotalHours >= 1
            ? $"{(int)eta.TotalHours}h {eta.Minutes}m"
            : $"{Math.Max(1, val2: (int)eta.TotalMinutes)} min";
        return $"~{span} at current rate";
    }

    /// <summary>
    /// Builds a daily-token-bars-grouped-by-model chart (docs/router/secrets-at-rest-plan.md §8.2): one category per
    /// reported day, one series per reported model.
    /// </summary>
    private static string ReportedUsageJson(ProviderReportedUsageAdminView usage)
    {
        var days = usage.Rows.Select(r => r.UsageDay).Distinct().OrderBy(d => d).ToList();
        var models = usage.Rows.Select(r => r.Model).Distinct()
            .OrderBy(keySelector: m => m, comparer: StringComparer.Ordinal).ToList();

        var series = models.Select((model, index) =>
        {
            var data = days.Select(day =>
            {
                var row = usage.Rows.FirstOrDefault(r => r.UsageDay == day && r.Model == model);
                return row is null
                    ? 0m
                    : row.InputTokens + row.OutputTokens + row.CacheCreationTokens + row.CacheReadTokens;
            }).ToList();
            return new DistributionSeries(Name: model,
                Color: ReportedUsageSeriesColors[index % ReportedUsageSeriesColors.Length], Data: data);
        }).ToList();

        var model = new GroupedBarsModel(
            title: "Daily Token Usage",
            categories: days.Select(d => d.ToString(format: "MM-dd", provider: CultureInfo.InvariantCulture)).ToList(),
            yMax: GroupedBarsModel.DynamicYMax(series.Select(s => s.Data)),
            series: series);
        return ChartJson.Serialize(model);
    }

    /// <summary>Serializes one rate-limit dimension's trend-chart-model JSON.</summary>
    private static string RateLimitTrendJson(string dimensionName, IReadOnlyList<RateLimitHistoryPointAdminView> points)
    {
        var model = RateLimitTrendChartBuilder.Build(
            dimensionName: dimensionName,
            points: points.Select(p => (p.BucketUtc, p.Remaining, p.Limit)).ToList());
        return ChartJson.Serialize(model);
    }

    /// <summary>Validates and submits the budget form's draft dollar/token caps, clearing the drafts on success.</summary>
    private async Task SaveBudget(string key)
    {
        _opError = null;

        var provider = Store.Providers.FirstOrDefault(p =>
            string.Equals(a: p.Key, b: key, comparisonType: StringComparison.OrdinalIgnoreCase));

        var dollarText = _draftDollarCap.TryGetValue(key: key, value: out var d)
            ? d
            : provider?.DollarCap?.ToString(CultureInfo.InvariantCulture);
        var tokenText = _draftTokenCap.TryGetValue(key: key, value: out var t)
            ? t
            : provider?.TokenCap?.ToString(CultureInfo.InvariantCulture);

        if (!TryParseDecimalCap(text: dollarText, value: out var dollarCap))
        {
            _opError = "Monthly $ cap must be a non-negative number, or blank for no cap.";
            return;
        }

        if (!TryParseLongCap(text: tokenText, value: out var tokenCap))
        {
            _opError = "Monthly token cap must be a non-negative whole number, or blank for no cap.";
            return;
        }

        if (await RunAsync(() => Store.SetBudgetAsync(key: key,
                body: new ProviderBudgetWriteRequest(DollarCap: dollarCap, TokenCap: tokenCap))))
        {
            // Drop the drafts so the inputs re-seed from the persisted values the refreshed store now holds.
            _draftDollarCap.Remove(key);
            _draftTokenCap.Remove(key);
        }
    }

    // The only two providers BuildCostReconcilers/ManagementFacade.SetSecret recognize
    // (docs/router/agent-cost-tracking.md §3.5) - matches the provider dictionary key convention under
    // CostTracking:Reconciliation:Providers, not ProviderType.
    /// <summary>Whether a provider key is one of the two providers cost reconciliation recognizes.</summary>
    private static bool IsRecognizedReconciliationProvider(string key)
    {
        return string.Equals(a: key, b: "openai", comparisonType: StringComparison.OrdinalIgnoreCase) ||
               string.Equals(a: key, b: "anthropic", comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The in-progress draft text for a provider's Admin API key field, or empty if none has been typed yet.</summary>
    private string GetAdminKeyDraft(string key)
    {
        return _draftAdminKey.TryGetValue(key: key, value: out var value) ? value : string.Empty;
    }

    /// <summary>Validates and stores the draft Admin API key for a provider, clearing the draft on success.</summary>
    private async Task SaveAdminApiKey(string key)
    {
        _opError = null;

        var value = GetAdminKeyDraft(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            _opError = "Admin API key must not be blank.";
            return;
        }

        if (await RunAsync(() => Store.SetAdminApiKeyAsync(provider: key, value: value))) _draftAdminKey.Remove(key);
    }

    /// <summary>Clears a provider's stored Admin API key.</summary>
    private async Task ClearAdminApiKey(string key)
    {
        if (await RunAsync(() => Store.DeleteAdminApiKeyAsync(key))) _draftAdminKey.Remove(key);
    }

    // Parses an optional decimal cap: blank -> null (no cap); a non-negative number -> that value; anything
    // else -> rejected (caller shows the error). Mirrors TryParseLongCap for the token dimension.
    /// <summary>
    /// Parses an optional decimal budget cap: blank means no cap, a non-negative number is accepted, anything else is
    /// rejected.
    /// </summary>
    private static bool TryParseDecimalCap(string? text, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (decimal.TryParse(s: text.Trim(), style: NumberStyles.Number, provider: CultureInfo.InvariantCulture,
                result: out var parsed) && parsed >= 0m)
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses an optional integer budget cap: blank means no cap, a non-negative whole number is accepted, anything
    /// else is rejected.
    /// </summary>
    private static bool TryParseLongCap(string? text, out long? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;

        if (long.TryParse(s: text.Trim(), style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture,
                result: out var parsed) && parsed >= 0)
        {
            value = parsed;
            return true;
        }

        return false;
    }
}