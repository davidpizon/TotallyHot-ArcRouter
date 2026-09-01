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
        var models = new List<ModelAdminView> { new("gpt-5.4", "gpt-5.4") };
        var headers = new List<ProviderHeaderView> { new("Authorization", HeaderValueSource.Literal, null) };

        var a = new ProviderAdminView("openai", "OpenAI API", "https://api.openai.com", "Authorization", models, headers);
        var b = new ProviderAdminView("openai", "OpenAI API", "https://api.openai.com", "Authorization", models, headers);
        var differentName = a with { Name = "Something Else" };

        Assert.Equal(a, b);
        Assert.NotEqual(a, differentName);
        Assert.Contains("openai", a.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ModelAdminView_RecordEquality_ComparesAllFields()
    {
        var a = new ModelAdminView("gpt-5.4", "gpt-5.4", "hermes", "Observed", true, true);
        var b = new ModelAdminView("gpt-5.4", "gpt-5.4", "hermes", "Observed", true, true);
        var differentDialect = a with { Dialect = "emulated" };

        Assert.Equal(a, b);
        Assert.NotEqual(a, differentDialect);
        Assert.Contains("gpt-5.4", a.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderEndpointCapabilitiesView_RecordEquality_ComparesAllFields()
    {
        var scannedAt = DateTimeOffset.Parse("2026-07-31T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var a = new ProviderEndpointCapabilitiesView("lmstudio", true, true, false, false, scannedAt, null);
        var b = new ProviderEndpointCapabilitiesView("lmstudio", true, true, false, false, scannedAt, null);
        var withError = a with { ScanError = "timed out" };

        Assert.Equal(a, b);
        Assert.NotEqual(a, withError);
        Assert.Contains("lmstudio", a.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderInteractionStatusAdminView_RecordEquality_ComparesAllFields()
    {
        var at = DateTimeOffset.Parse("2026-07-31T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var a = new ProviderInteractionStatusAdminView(false, "Refresh from endpoint", "Provider returned 401.", at);
        var b = new ProviderInteractionStatusAdminView(false, "Refresh from endpoint", "Provider returned 401.", at);
        var nowOk = a with { Ok = true, Message = null };

        Assert.Equal(a, b);
        Assert.NotEqual(a, nowOk);
        Assert.Contains("Refresh from endpoint", a.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderAdminView_AdminActionAndLiveTraffic_DefaultToNull()
    {
        var view = new ProviderAdminView("openai", "OpenAI API", "https://api.openai.com", "Authorization", [], []);

        Assert.Null(view.AdminAction);
        Assert.Null(view.LiveTraffic);
    }

    [Fact]
    public void ProviderAdminView_RoundTripsAdminAction()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var at = DateTimeOffset.Parse("2026-08-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var view = new ProviderAdminView(
            "openai",
            "OpenAI API",
            "https://api.openai.com",
            "Authorization",
            [],
            [],
            AdminAction: new ProviderInteractionStatusAdminView(false, "Refresh from endpoint", "Provider returned 401 for https://api.openai.com/v1/models.", at));

        var json = System.Text.Json.JsonSerializer.Serialize(view, options);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ProviderAdminView>(json, options)!;

        Assert.NotNull(roundTripped.AdminAction);
        Assert.False(roundTripped.AdminAction!.Ok);
        Assert.Equal("Refresh from endpoint", roundTripped.AdminAction.Operation);
        Assert.Equal(view.AdminAction!.Message, roundTripped.AdminAction.Message);
        Assert.Equal(view.AdminAction!.AtUtc, roundTripped.AdminAction.AtUtc);
    }

    [Fact]
    public void ProviderAdminView_RoundTripsLiveTraffic()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var at = DateTimeOffset.Parse("2026-08-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var view = new ProviderAdminView(
            "openai",
            "OpenAI API",
            "https://api.openai.com",
            "Authorization",
            [],
            [],
            LiveTraffic: new ProviderInteractionStatusAdminView(
                false,
                "Live traffic",
                "Your credit balance is too low.",
                at,
                ProviderInteractionKindAdminView.OutOfCredits));

        var json = System.Text.Json.JsonSerializer.Serialize(view, options);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ProviderAdminView>(json, options)!;

        Assert.NotNull(roundTripped.LiveTraffic);
        Assert.False(roundTripped.LiveTraffic!.Ok);
        Assert.Equal(ProviderInteractionKindAdminView.OutOfCredits, roundTripped.LiveTraffic.Kind);
        Assert.Equal(view.LiveTraffic!.Message, roundTripped.LiveTraffic.Message);
    }

    [Fact]
    public void ProviderHeaderWriteModel_RecordEquality_ComparesAllFields()
    {
        var a = new ProviderHeaderWriteModel("anthropic-version", "2023-06-01", null);
        var b = new ProviderHeaderWriteModel("anthropic-version", "2023-06-01", null);
        var differentValue = a with { Value = "2024-01-01" };
        var locked = a with { Locked = true };

        Assert.Equal(a, b);
        Assert.NotEqual(a, differentValue);
        Assert.NotEqual(a, locked);
        Assert.Contains("anthropic-version", a.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderHeaderView_RoundTripsTheSecretFieldMembers()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var unlocked = System.Text.Json.JsonSerializer.Deserialize<ProviderHeaderView>(
            """{"name":"anthropic-version","source":"literal","valueEnvVar":null,"value":"2023-06-01","locked":false}""",
            options)!;
        var locked = System.Text.Json.JsonSerializer.Deserialize<ProviderHeaderView>(
            """{"name":"X-Subscription-Key","source":"literal","valueEnvVar":null,"value":null,"locked":true}""",
            options)!;

        Assert.Equal("2023-06-01", unlocked.Value);
        Assert.False(unlocked.Locked);
        // The router drops a locked value before it reaches the wire, so the view carries the flag alone.
        Assert.Null(locked.Value);
        Assert.True(locked.Locked);
    }

    [Fact]
    public void ProviderHeaderWriteModel_OmitsTheLockFlagWhenItIsNull()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        var json = System.Text.Json.JsonSerializer.Serialize(
            new ProviderHeaderWriteModel("X-Test", "hello", null, Locked: false), options);

        // An explicit false is what tells the server a blank value means "clear", so it must survive
        // serialization rather than being treated as an absent default.
        Assert.Contains("\"locked\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderAdminView_RoundTripsUsageLastRecordedAtUtcAndRateLimit()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var view = new ProviderAdminView(
            "anthropic",
            "Anthropic Prod",
            "https://api.anthropic.com",
            "x-api-key",
            [],
            [],
            UsageLastRecordedAtUtc: DateTimeOffset.Parse("2026-03-01T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            RateLimit: new ProviderRateLimitAdminView(
                new RateLimitSnapshotAdminView(
                    new Dictionary<string, RateLimitDimensionAdminView>
                    {
                        ["tokens"] = new(200000, 158000, DateTimeOffset.Parse("2026-03-01T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
                    },
                    "allowed",
                    null,
                    new Dictionary<string, RateLimitWindowAdminView> { ["5h"] = new("allowed", null, null) },
                    "org-123",
                    new Dictionary<string, string> { ["anthropic-ratelimit-tokens-remaining"] = "158000" }),
                DateTimeOffset.Parse("2026-03-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));

        var json = System.Text.Json.JsonSerializer.Serialize(view, options);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ProviderAdminView>(json, options)!;

        Assert.Equal(view.UsageLastRecordedAtUtc, roundTripped.UsageLastRecordedAtUtc);
        Assert.NotNull(roundTripped.RateLimit);
        Assert.Equal(view.RateLimit!.ObservedAtUtc, roundTripped.RateLimit!.ObservedAtUtc);
        Assert.Equal(200000, roundTripped.RateLimit.Snapshot.StandardDimensions["tokens"].Limit);
        Assert.Equal("allowed", roundTripped.RateLimit.Snapshot.UnifiedWindows["5h"].Status);
        Assert.Equal("org-123", roundTripped.RateLimit.Snapshot.RepresentativeClaim);
        Assert.Equal("158000", roundTripped.RateLimit.Snapshot.RawHeaders["anthropic-ratelimit-tokens-remaining"]);
    }

    [Fact]
    public void ProviderAdminView_UsageLastRecordedAtUtcAndRateLimit_DefaultToNull()
    {
        var view = new ProviderAdminView("openai", "OpenAI API", "https://api.openai.com", "Authorization", [], []);

        Assert.Null(view.UsageLastRecordedAtUtc);
        Assert.Null(view.RateLimit);
    }

    [Fact]
    public void ProviderAdminView_HasStoredAdminKey_DefaultsToFalse()
    {
        var view = new ProviderAdminView("anthropic", "Anthropic", "https://api.anthropic.com", "x-api-key", [], []);

        Assert.False(view.HasStoredAdminKey);
    }

    [Fact]
    public void ProviderAdminView_RoundTripsHasStoredAdminKeyAndReportedUsage()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var view = new ProviderAdminView(
            "anthropic",
            "Anthropic Prod",
            "https://api.anthropic.com",
            "x-api-key",
            [],
            [],
            HasStoredAdminKey: true,
            ReportedUsage: new ProviderReportedUsageAdminView(
                [new ReportedUsageRowAdminView(new DateOnly(2026, 3, 1), "claude-opus-4-1", 100, 50, 5, 10)],
                DateTimeOffset.Parse("2026-03-02T04:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));

        var json = System.Text.Json.JsonSerializer.Serialize(view, options);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ProviderAdminView>(json, options)!;

        Assert.True(roundTripped.HasStoredAdminKey);
        Assert.NotNull(roundTripped.ReportedUsage);
        Assert.Equal(view.ReportedUsage!.FetchedAtUtc, roundTripped.ReportedUsage!.FetchedAtUtc);
        var row = Assert.Single(roundTripped.ReportedUsage.Rows);
        Assert.Equal(new DateOnly(2026, 3, 1), row.UsageDay);
        Assert.Equal("claude-opus-4-1", row.Model);
        Assert.Equal(100, row.InputTokens);
        Assert.Equal(5, row.CacheCreationTokens);
    }

    [Fact]
    public void ProviderTemplates_HasATemplateForEveryProviderType()
    {
        foreach (var providerType in Enum.GetValues<ProviderType>())
        {
            Assert.True(
                ProviderTemplates.Templates.ContainsKey(providerType),
                $"No template registered for {providerType}.");
        }
    }

    [Fact]
    public void ProviderTemplates_Ordered_ListsEveryProviderTypeExactlyOnce()
    {
        // The editor renders its dropdown from Ordered, so a type missing here is a type the operator can
        // never select - and one listed twice is a duplicated option.
        Assert.Equal(
            Enum.GetValues<ProviderType>().OrderBy(t => t).ToList(),
            ProviderTemplates.Ordered.OrderBy(t => t).ToList());
        Assert.Equal(ProviderTemplates.Ordered.Count, ProviderTemplates.Ordered.Distinct().Count());
    }

    [Fact]
    public void ProviderTemplates_Ordered_PutsOtherLast()
    {
        Assert.Equal(ProviderType.Other, ProviderTemplates.Ordered[^1]);
    }

    [Fact]
    public void ProviderTemplates_DisplayName_LabelsFamiliesRatherThanEnumNames()
    {
        Assert.Equal("OpenAI / Groq / DeepSeek", ProviderTemplates.DisplayName(ProviderType.OpenAI));
        Assert.Equal("Ollama / LM Studio / llama.cpp", ProviderTemplates.DisplayName(ProviderType.LocalRuntime));
        Assert.Equal("Anthropic", ProviderTemplates.DisplayName(ProviderType.Anthropic));
    }

    [Fact]
    public void ProviderTemplates_Anthropic_UsesTheApiKeyHeaderAndSuppliesTheVersionHeader()
    {
        var template = ProviderTemplates.Templates[ProviderType.Anthropic];

        Assert.Equal("https://api.anthropic.com", template.BaseUrl);
        Assert.True(template.RequiresAuth);
        Assert.Equal("x-api-key", template.AuthHeaderName);
        // A raw key, no scheme prefix - so the suggestion is a bare variable name.
        Assert.Equal("ANTHROPIC_API_KEY", template.SuggestedEnvValue);
        // Without anthropic-version every request 400s, which used to be the operator's problem to discover.
        var header = Assert.Single(template.DefaultHeaders);
        Assert.Equal("anthropic-version", header.Name);
        Assert.Equal("2023-06-01", header.Value);
    }

    [Fact]
    public void ProviderTemplates_OpenAI_SuggestsABearerValueTemplate()
    {
        var template = ProviderTemplates.Templates[ProviderType.OpenAI];

        Assert.Equal("https://api.openai.com/v1", template.BaseUrl);
        Assert.True(template.RequiresAuth);
        Assert.Equal("Authorization", template.AuthHeaderName);
        Assert.Equal("Bearer {env:OPENAI_API_KEY}", template.SuggestedEnvValue);
        Assert.Empty(template.DefaultHeaders);
    }

    [Fact]
    public void ProviderTemplates_Other_RequiresACredential()
    {
        var template = ProviderTemplates.Templates[ProviderType.Other];

        Assert.Equal(string.Empty, template.BaseUrl);
        // "Other" now means an unknown *remote* API: every unauthenticated case has its own type.
        Assert.True(template.RequiresAuth);
        Assert.Equal("Authorization", template.AuthHeaderName);
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
            AuthValueTemplate.TryParse(template.SuggestedEnvValue, out _, out _, out var error),
            $"{providerType}'s suggested value does not parse: {error}");
    }

    [Fact]
    public void ToolCallDialectNames_All_ListsEveryKnownDialect()
    {
        Assert.Equal(
            ["openai-native", "constrained", "emulated", "hermes", "mistral", "llama3-json", "function-call"],
            ToolCallDialectNames.All);
    }

    [Fact]
    public void ProviderAdminException_SingleArgConstructor_SetsMessage()
    {
        var ex = new ProviderAdminException("boom");

        Assert.Equal("boom", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void ProviderAdminException_TwoArgConstructor_SetsMessageAndInnerException()
    {
        var inner = new InvalidOperationException("transport failed");

        var ex = new ProviderAdminException("boom", inner);

        Assert.Equal("boom", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}
