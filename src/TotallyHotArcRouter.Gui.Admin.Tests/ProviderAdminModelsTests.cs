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

        var a = new ProviderAdminView("openai", "OpenAI API", "https://api.openai.com", "Authorization", "Bearer", true, null, models, headers);
        var b = new ProviderAdminView("openai", "OpenAI API", "https://api.openai.com", "Authorization", "Bearer", true, null, models, headers);
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

        Assert.Equal(a, b);
        Assert.NotEqual(a, differentValue);
        Assert.Contains("anthropic-version", a.ToString(), StringComparison.Ordinal);
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
    public void ProviderTemplates_Anthropic_UsesTheApiKeyHeader()
    {
        var template = ProviderTemplates.Templates[ProviderType.Anthropic];

        Assert.Equal("https://api.anthropic.com", template.BaseUrl);
        Assert.Equal("x-api-key", template.AuthHeaderName);
        Assert.Equal(string.Empty, template.AuthHeaderScheme);
        Assert.Equal(ProviderCredentialModes.Literal, template.DefaultCredentialMode);
    }

    [Fact]
    public void ProviderTemplates_OpenAI_UsesTheBearerScheme()
    {
        var template = ProviderTemplates.Templates[ProviderType.OpenAI];

        Assert.Equal("https://api.openai.com/v1", template.BaseUrl);
        Assert.Equal("Authorization", template.AuthHeaderName);
        Assert.Equal("Bearer", template.AuthHeaderScheme);
        Assert.Equal(ProviderCredentialModes.Literal, template.DefaultCredentialMode);
    }

    [Fact]
    public void ProviderTemplates_Other_HasNoDefaultCredential()
    {
        var template = ProviderTemplates.Templates[ProviderType.Other];

        Assert.Equal(string.Empty, template.BaseUrl);
        Assert.Equal(ProviderCredentialModes.None, template.DefaultCredentialMode);
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
