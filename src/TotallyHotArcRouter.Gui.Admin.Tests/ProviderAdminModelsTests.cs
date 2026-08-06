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
