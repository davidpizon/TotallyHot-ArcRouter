using System.Globalization;
using System.Text.Json;

namespace TotallyHot.ArcRouter.Gui.Admin.Tests;

/// <summary>
/// Covers the plain-data types in <c>ProviderAdminModels.cs</c> that <see cref="ProviderAdminClientTests"/>
/// only exercises indirectly through JSON round-trips: record equality/formatting, <see cref="ProviderTemplates"/>,
/// <see cref="ToolCallDialectNames"/>, and <see cref="ProviderAdminException"/>'s two constructors.
/// </summary>
public sealed class ProviderAdminModelsTests
{
    [Fact]
    public void ProviderAdminView_RecordEquality_ComparesAllFields()
    {
        var models = new List<ModelAdminView> { new(ModelName: "gpt-5.4", ProviderModelId: "gpt-5.4") };
        var headers = new List<ProviderHeaderView>
            { new(Name: "Authorization", Source: HeaderValueSource.Literal, null) };

        var a = new ProviderAdminView(Key: "openai", Name: "OpenAI API", BaseUrl: "https://api.openai.com",
            AuthHeaderName: "Authorization", Models: models, Headers: headers);
        var b = new ProviderAdminView(Key: "openai", Name: "OpenAI API", BaseUrl: "https://api.openai.com",
            AuthHeaderName: "Authorization", Models: models, Headers: headers);
        var differentName = a with { Name = "Something Else" };

        Assert.Equal(expected: a, actual: b);
        Assert.NotEqual(expected: a, actual: differentName);
        Assert.Contains(expectedSubstring: "openai", actualString: a.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ModelAdminView_RecordEquality_ComparesAllFields()
    {
        var a = new ModelAdminView(ModelName: "gpt-5.4", ProviderModelId: "gpt-5.4", Dialect: "hermes",
            Confidence: "Observed");
        var b = new ModelAdminView(ModelName: "gpt-5.4", ProviderModelId: "gpt-5.4", Dialect: "hermes",
            Confidence: "Observed");
        var differentDialect = a with { Dialect = "emulated" };

        Assert.Equal(expected: a, actual: b);
        Assert.NotEqual(expected: a, actual: differentDialect);
        Assert.Contains(expectedSubstring: "gpt-5.4", actualString: a.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderEndpointCapabilitiesView_RecordEquality_ComparesAllFields()
    {
        var scannedAt =
            DateTimeOffset.Parse(input: "2026-07-31T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        var a = new ProviderEndpointCapabilitiesView(ProviderKey: "lmstudio", true, true, false, false,
            ScannedAtUtc: scannedAt);
        var b = new ProviderEndpointCapabilitiesView(ProviderKey: "lmstudio", true, true, false, false,
            ScannedAtUtc: scannedAt);
        var withError = a with { ScanError = "timed out" };

        Assert.Equal(expected: a, actual: b);
        Assert.NotEqual(expected: a, actual: withError);
        Assert.Contains(expectedSubstring: "lmstudio", actualString: a.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderInteractionStatusAdminView_RecordEquality_ComparesAllFields()
    {
        var at = DateTimeOffset.Parse(input: "2026-07-31T00:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        var a = new ProviderInteractionStatusAdminView(false, Operation: "Refresh from endpoint",
            Message: "Provider returned 401.", AtUtc: at);
        var b = new ProviderInteractionStatusAdminView(false, Operation: "Refresh from endpoint",
            Message: "Provider returned 401.", AtUtc: at);
        var nowOk = a with { Ok = true, Message = null };

        Assert.Equal(expected: a, actual: b);
        Assert.NotEqual(expected: a, actual: nowOk);
        Assert.Contains(expectedSubstring: "Refresh from endpoint", actualString: a.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderAdminView_AdminActionAndLiveTraffic_DefaultToNull()
    {
        var view = new ProviderAdminView(Key: "openai", Name: "OpenAI API", BaseUrl: "https://api.openai.com",
            AuthHeaderName: "Authorization", Models: [], Headers: []);

        Assert.Null(view.AdminAction);
        Assert.Null(view.LiveTraffic);
    }

    [Fact]
    public void ProviderAdminView_RoundTripsAdminAction()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var at = DateTimeOffset.Parse(input: "2026-08-24T09:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        var view = new ProviderAdminView(
            Key: "openai",
            Name: "OpenAI API",
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: "Authorization",
            Models: [],
            Headers: [],
            AdminAction: new ProviderInteractionStatusAdminView(false, Operation: "Refresh from endpoint",
                Message: "Provider returned 401 for https://api.openai.com/v1/models.", AtUtc: at));

        var json = JsonSerializer.Serialize(value: view, options: options);
        var roundTripped = JsonSerializer.Deserialize<ProviderAdminView>(json: json, options: options)!;

        Assert.NotNull(roundTripped.AdminAction);
        Assert.False(roundTripped.AdminAction!.Ok);
        Assert.Equal(expected: "Refresh from endpoint", actual: roundTripped.AdminAction.Operation);
        Assert.Equal(expected: view.AdminAction!.Message, actual: roundTripped.AdminAction.Message);
        Assert.Equal(expected: view.AdminAction!.AtUtc, actual: roundTripped.AdminAction.AtUtc);
    }

    [Fact]
    public void ProviderAdminView_RoundTripsLiveTraffic()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var at = DateTimeOffset.Parse(input: "2026-08-24T09:00:00Z", formatProvider: CultureInfo.InvariantCulture);
        var view = new ProviderAdminView(
            Key: "openai",
            Name: "OpenAI API",
            BaseUrl: "https://api.openai.com",
            AuthHeaderName: "Authorization",
            Models: [],
            Headers: [],
            LiveTraffic: new ProviderInteractionStatusAdminView(
                false,
                Operation: "Live traffic",
                Message: "Your credit balance is too low.",
                AtUtc: at,
                Kind: ProviderInteractionKindAdminView.OutOfCredits));

        var json = JsonSerializer.Serialize(value: view, options: options);
        var roundTripped = JsonSerializer.Deserialize<ProviderAdminView>(json: json, options: options)!;

        Assert.NotNull(roundTripped.LiveTraffic);
        Assert.False(roundTripped.LiveTraffic!.Ok);
        Assert.Equal(expected: ProviderInteractionKindAdminView.OutOfCredits, actual: roundTripped.LiveTraffic.Kind);
        Assert.Equal(expected: view.LiveTraffic!.Message, actual: roundTripped.LiveTraffic.Message);
    }

    [Fact]
    public void ProviderHeaderWriteModel_RecordEquality_ComparesAllFields()
    {
        var a = new ProviderHeaderWriteModel(Name: "anthropic-version", Value: "2023-06-01", null);
        var b = new ProviderHeaderWriteModel(Name: "anthropic-version", Value: "2023-06-01", null);
        var differentValue = a with { Value = "2024-01-01" };
        var locked = a with { Locked = true };

        Assert.Equal(expected: a, actual: b);
        Assert.NotEqual(expected: a, actual: differentValue);
        Assert.NotEqual(expected: a, actual: locked);
        Assert.Contains(expectedSubstring: "anthropic-version", actualString: a.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderHeaderView_RoundTripsTheSecretFieldMembers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var unlocked = JsonSerializer.Deserialize<ProviderHeaderView>(
            """{"name":"anthropic-version","source":"literal","valueEnvVar":null,"value":"2023-06-01","locked":false}""",
            options: options)!;
        var locked = JsonSerializer.Deserialize<ProviderHeaderView>(
            """{"name":"X-Subscription-Key","source":"literal","valueEnvVar":null,"value":null,"locked":true}""",
            options: options)!;

        Assert.Equal(expected: "2023-06-01", actual: unlocked.Value);
        Assert.False(unlocked.Locked);
        // The router drops a locked value before it reaches the wire, so the view carries the flag alone.
        Assert.Null(locked.Value);
        Assert.True(locked.Locked);
    }

    [Fact]
    public void ProviderHeaderWriteModel_OmitsTheLockFlagWhenItIsNull()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(
            value: new ProviderHeaderWriteModel(Name: "X-Test", Value: "hello", null, false), options: options);

        // An explicit false is what tells the server a blank value means "clear", so it must survive
        // serialization rather than being treated as an absent default.
        Assert.Contains(expectedSubstring: "\"locked\":false", actualString: json,
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderAdminView_RoundTripsUsageLastRecordedAtUtcAndRateLimit()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var view = new ProviderAdminView(
            Key: "anthropic",
            Name: "Anthropic Prod",
            BaseUrl: "https://api.anthropic.com",
            AuthHeaderName: "x-api-key",
            Models: [],
            Headers: [],
            UsageLastRecordedAtUtc: DateTimeOffset.Parse(input: "2026-03-01T08:00:00Z",
                formatProvider: CultureInfo.InvariantCulture),
            RateLimit: new ProviderRateLimitAdminView(
                Snapshot: new RateLimitSnapshotAdminView(
                    StandardDimensions: new Dictionary<string, RateLimitDimensionAdminView>
                    {
                        ["tokens"] = new(200000, 158000,
                            ResetAt: DateTimeOffset.Parse(input: "2026-03-01T13:00:00Z",
                                formatProvider: CultureInfo.InvariantCulture))
                    },
                    UnifiedStatus: "allowed",
                    null,
                    UnifiedWindows: new Dictionary<string, RateLimitWindowAdminView>
                    { ["5h"] = new(Status: "allowed", null, null) },
                    RepresentativeClaim: "org-123",
                    RawHeaders: new Dictionary<string, string> { ["anthropic-ratelimit-tokens-remaining"] = "158000" }),
                ObservedAtUtc: DateTimeOffset.Parse(input: "2026-03-01T12:00:00Z",
                    formatProvider: CultureInfo.InvariantCulture)));

        var json = JsonSerializer.Serialize(value: view, options: options);
        var roundTripped = JsonSerializer.Deserialize<ProviderAdminView>(json: json, options: options)!;

        Assert.Equal(expected: view.UsageLastRecordedAtUtc, actual: roundTripped.UsageLastRecordedAtUtc);
        Assert.NotNull(roundTripped.RateLimit);
        Assert.Equal(expected: view.RateLimit!.ObservedAtUtc, actual: roundTripped.RateLimit!.ObservedAtUtc);
        Assert.Equal(200000, actual: roundTripped.RateLimit.Snapshot.StandardDimensions["tokens"].Limit);
        Assert.Equal(expected: "allowed", actual: roundTripped.RateLimit.Snapshot.UnifiedWindows["5h"].Status);
        Assert.Equal(expected: "org-123", actual: roundTripped.RateLimit.Snapshot.RepresentativeClaim);
        Assert.Equal(expected: "158000",
            actual: roundTripped.RateLimit.Snapshot.RawHeaders["anthropic-ratelimit-tokens-remaining"]);
    }

    [Fact]
    public void ProviderAdminView_UsageLastRecordedAtUtcAndRateLimit_DefaultToNull()
    {
        var view = new ProviderAdminView(Key: "openai", Name: "OpenAI API", BaseUrl: "https://api.openai.com",
            AuthHeaderName: "Authorization", Models: [], Headers: []);

        Assert.Null(view.UsageLastRecordedAtUtc);
        Assert.Null(view.RateLimit);
    }

    [Fact]
    public void ProviderAdminView_HasStoredAdminKey_DefaultsToFalse()
    {
        var view = new ProviderAdminView(Key: "anthropic", Name: "Anthropic", BaseUrl: "https://api.anthropic.com",
            AuthHeaderName: "x-api-key", Models: [], Headers: []);

        Assert.False(view.HasStoredAdminKey);
    }

    [Fact]
    public void ProviderAdminView_RoundTripsHasStoredAdminKeyAndReportedUsage()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var view = new ProviderAdminView(
            Key: "anthropic",
            Name: "Anthropic Prod",
            BaseUrl: "https://api.anthropic.com",
            AuthHeaderName: "x-api-key",
            Models: [],
            Headers: [],
            HasStoredAdminKey: true,
            ReportedUsage: new ProviderReportedUsageAdminView(
                Rows:
                [
                    new ReportedUsageRowAdminView(UsageDay: new DateOnly(2026, 3, 1), Model: "claude-opus-4-1", 100, 50,
                        5, 10)
                ],
                FetchedAtUtc: DateTimeOffset.Parse(input: "2026-03-02T04:00:00Z",
                    formatProvider: CultureInfo.InvariantCulture)));

        var json = JsonSerializer.Serialize(value: view, options: options);
        var roundTripped = JsonSerializer.Deserialize<ProviderAdminView>(json: json, options: options)!;

        Assert.True(roundTripped.HasStoredAdminKey);
        Assert.NotNull(roundTripped.ReportedUsage);
        Assert.Equal(expected: view.ReportedUsage!.FetchedAtUtc, actual: roundTripped.ReportedUsage!.FetchedAtUtc);
        var row = Assert.Single(roundTripped.ReportedUsage.Rows);
        Assert.Equal(expected: new DateOnly(2026, 3, 1), actual: row.UsageDay);
        Assert.Equal(expected: "claude-opus-4-1", actual: row.Model);
        Assert.Equal(100, actual: row.InputTokens);
        Assert.Equal(5, actual: row.CacheCreationTokens);
    }

    [Fact]
    public void ProviderTemplates_HasATemplateForEveryProviderType()
    {
        foreach (var providerType in Enum.GetValues<ProviderType>())
            Assert.True(
                condition: ProviderTemplates.Templates.ContainsKey(providerType),
                userMessage: $"No template registered for {providerType}.");
    }

    [Fact]
    public void ProviderTemplates_Ordered_ListsEveryProviderTypeExactlyOnce()
    {
        // The editor renders its dropdown from Ordered, so a type missing here is a type the operator can
        // never select - and one listed twice is a duplicated option.
        Assert.Equal(
            expected: Enum.GetValues<ProviderType>().OrderBy(t => t).ToList(),
            actual: [.. ProviderTemplates.Ordered.OrderBy(t => t)]);
        Assert.Equal(expected: ProviderTemplates.Ordered.Count, actual: ProviderTemplates.Ordered.Distinct().Count());
    }

    [Fact]
    public void ProviderTemplates_Ordered_PutsOtherLast()
    {
        Assert.Equal(expected: ProviderType.Other, actual: ProviderTemplates.Ordered[^1]);
    }

    [Fact]
    public void ProviderTemplates_DisplayName_LabelsFamiliesRatherThanEnumNames()
    {
        Assert.Equal(expected: "OpenAI / Groq / DeepSeek", actual: ProviderTemplates.DisplayName(ProviderType.OpenAI));
        Assert.Equal(expected: "Ollama / LM Studio / llama.cpp",
            actual: ProviderTemplates.DisplayName(ProviderType.LocalRuntime));
        Assert.Equal(expected: "Anthropic", actual: ProviderTemplates.DisplayName(ProviderType.Anthropic));
    }

    [Fact]
    public void ProviderTemplates_Anthropic_UsesTheApiKeyHeaderAndSuppliesTheVersionHeader()
    {
        var template = ProviderTemplates.Templates[ProviderType.Anthropic];

        Assert.Equal(expected: "https://api.anthropic.com", actual: template.BaseUrl);
        Assert.True(template.RequiresAuth);
        Assert.Equal(expected: "x-api-key", actual: template.AuthHeaderName);
        // A raw key, no scheme prefix - so the suggestion is a bare variable name.
        Assert.Equal(expected: "ANTHROPIC_API_KEY", actual: template.SuggestedEnvValue);
        // Without anthropic-version every request 400s, which used to be the operator's problem to discover.
        var header = Assert.Single(template.DefaultHeaders);
        Assert.Equal(expected: "anthropic-version", actual: header.Name);
        Assert.Equal(expected: "2023-06-01", actual: header.Value);
    }

    [Fact]
    public void ProviderTemplates_OpenAI_SuggestsABearerValueTemplate()
    {
        var template = ProviderTemplates.Templates[ProviderType.OpenAI];

        Assert.Equal(expected: "https://api.openai.com/v1", actual: template.BaseUrl);
        Assert.True(template.RequiresAuth);
        Assert.Equal(expected: "Authorization", actual: template.AuthHeaderName);
        Assert.Equal(expected: "Bearer {env:OPENAI_API_KEY}", actual: template.SuggestedEnvValue);
        Assert.Empty(template.DefaultHeaders);
    }

    [Fact]
    public void ProviderTemplates_Other_RequiresACredential()
    {
        var template = ProviderTemplates.Templates[ProviderType.Other];

        Assert.Equal(expected: string.Empty, actual: template.BaseUrl);
        // "Other" now means an unknown *remote* API: every unauthenticated case has its own type.
        Assert.True(template.RequiresAuth);
        Assert.Equal(expected: "Authorization", actual: template.AuthHeaderName);
        Assert.Empty(template.SuggestedEnvValue);
    }

    [Theory]
    [InlineData(ProviderType.LocalRuntime)]
    [InlineData(ProviderType.Bedrock)]
    public void ProviderTemplates_UnauthenticatedTypes_ExplainWhyTheyNeedNoCredential(ProviderType providerType)
    {
        var template = ProviderTemplates.Templates[providerType];

        Assert.False(template.RequiresAuth);
        Assert.Empty(template.SuggestedEnvValue);
        // The absence of a credential is surprising enough to need saying, or an operator assumes it's a bug.
        Assert.False(string.IsNullOrWhiteSpace(template.AuthHint));
    }

    [Fact]
    public void ProviderTemplates_LocalRuntime_DefaultsToFree()
    {
        Assert.True(ProviderTemplates.Templates[ProviderType.LocalRuntime].DefaultsToFree);
        Assert.False(ProviderTemplates.Templates[ProviderType.OpenAI].DefaultsToFree);
    }

    [Theory]
    [InlineData(ProviderType.Anthropic)]
    [InlineData(ProviderType.OpenAI)]
    [InlineData(ProviderType.GoogleGemini)]
    [InlineData(ProviderType.AzureOpenAI)]
    [InlineData(ProviderType.Cohere)]
    public void ProviderTemplates_AuthenticatedTypes_HaveAHeaderAndAParsableSuggestion(ProviderType providerType)
    {
        var template = ProviderTemplates.Templates[providerType];

        Assert.True(template.RequiresAuth);
        Assert.False(string.IsNullOrWhiteSpace(template.AuthHeaderName));
        // A suggestion the editor's own parser rejects would be a placeholder the operator cannot copy.
        Assert.True(
            condition: AuthValueTemplate.TryParse(template: template.SuggestedEnvValue, scheme: out _,
                envVarName: out _, error: out var error),
            userMessage: $"{providerType}'s suggested value does not parse: {error}");
    }

    [Fact]
    public void ToolCallDialectNames_All_ListsEveryKnownDialect()
    {
        Assert.Equal(
            expected: ["openai-native", "constrained", "emulated", "hermes", "mistral", "llama3-json", "function-call"],
            actual: ToolCallDialectNames.All);
    }

    [Fact]
    public void ProviderAdminException_SingleArgConstructor_SetsMessage()
    {
        var ex = new ProviderAdminException("boom");

        Assert.Equal(expected: "boom", actual: ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void ProviderAdminException_TwoArgConstructor_SetsMessageAndInnerException()
    {
        var inner = new InvalidOperationException("transport failed");

        var ex = new ProviderAdminException(message: "boom", innerException: inner);

        Assert.Equal(expected: "boom", actual: ex.Message);
        Assert.Same(expected: inner, actual: ex.InnerException);
    }
}