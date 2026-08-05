using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Components;
using Bunit;
using FluentAssertions;

namespace TotallyHot.ArcRouter.Gui.Tests;

/// <summary>
/// Component tests for <see cref="ProviderEditDialog"/>. The headline test reproduces the "Save does
/// nothing" bug: the dialog is hosted (via Governance → ProvidersAdmin) under Dashboard, which calls
/// <c>StateHasChanged</c> on every live-telemetry tick. Each of those parent re-renders re-runs the
/// dialog's parameter-set lifecycle, so seeding the editable fields there (the old
/// <c>OnParametersSet</c>) silently reverted the user's in-progress edits between typing and Save.
/// Seeding once in <c>OnInitialized</c> fixes it; <see cref="Edit_survives_parent_rerender_before_save"/>
/// fails against the old code and passes against the fix.
/// <para>
/// Controls are located by <c>data-testid</c> rather than by position. The previous
/// <c>FindAll("select")[1]</c> lookups broke the moment a field was added above them, which is exactly
/// what the Credentials fieldset did.
/// </para>
/// </summary>
public sealed class ProviderEditDialogTests
{
    private const string Key = "anthropic";
    private const string OriginalBaseUrl = "https://api.anthropic.com";
    private const string EditedBaseUrl = "https://api.anthropic.com/edited";

    [Fact]
    public void Renders_the_edit_provider_title()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(SeedEditParameters);

        // The edit dialog shows "Edit Provider" as the title, not the specific provider key.
        cut.Markup.Should().Contain("Edit Provider");
    }

    [Fact]
    public void Edit_survives_parent_rerender_before_save()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        // The user edits the Base URL (the component binds @oninput, so dispatch an input event).
        cut.Find("[data-testid='base-url']").Input(EditedBaseUrl);

        // A live-telemetry tick re-renders the parent, which re-supplies the dialog's ORIGINAL parameters.
        // This is the exact event that used to clobber the in-progress edit.
        cut.Render(SeedEditParameters);

        // The user clicks Save.
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.Key.Should().Be(Key);
        saved.BaseUrl.Should().Be(EditedBaseUrl);
    }

    [Fact]
    public void Save_sends_the_edited_value_without_an_intervening_rerender()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.Find("[data-testid='base-url']").Input(EditedBaseUrl);
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.BaseUrl.Should().Be(EditedBaseUrl);
    }

    [Fact]
    public void Editing_a_provider_with_a_stored_key_shows_the_saved_indicator()
    {
        using var ctx = new Bunit.BunitContext();

        // HasApiKey: true (set by SeedEditParameters) opens the primary credential as a literal value for an
        // existing provider. The secret itself is never sent to the client, so the field is blank - the
        // indicator is the only cue a key exists.
        var cut = ctx.Render<ProviderEditDialog>(SeedEditParameters);

        cut.Markup.Should().Contain("A key is saved");
    }

    [Fact]
    public void Adding_a_new_provider_does_not_show_the_saved_indicator()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => parameters
            .Add(p => p.IsNew, true)
            .Add(p => p.HasApiKey, false));

        cut.Markup.Should().NotContain("A key is saved");
    }

    [Fact]
    public void Typing_a_replacement_key_hides_the_saved_indicator()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(SeedEditParameters);
        cut.Markup.Should().Contain("A key is saved");

        // Once the user types a new key, the "leave blank to keep" cue no longer applies.
        cut.Find("[data-testid='credential-value-0']").Input("sk-replacement");

        cut.Markup.Should().NotContain("A key is saved");
    }

    [Fact]
    public void Free_provider_checkbox_seeds_from_the_parameter()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.IsFree, true);
        });

        cut.Find("[data-testid='is-free']").HasAttribute("checked").Should().BeTrue();
    }

    // The flag rides the same OnInitialized-only seeding as every other field, so it must survive a
    // parent re-render mid-edit for the same reason (see this class's summary).
    [Fact]
    public void Ticking_free_provider_emits_it_on_save()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.Find("[data-testid='is-free']").Change(true);
        cut.Render(SeedEditParameters);
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.IsFree.Should().BeTrue();
    }

    [Fact]
    public void Requires_authentication_seeds_ticked_for_a_provider_with_a_stored_key()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(SeedEditParameters);

        cut.Find("[data-testid='requires-auth']").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void Requires_authentication_seeds_unticked_for_a_provider_with_no_credential()
    {
        using var ctx = new Bunit.BunitContext();

        // A deliberately unauthenticated provider (a local Ollama, say) must come back unticked rather than
        // inheriting the selected type's default - what is stored wins over any template on edit.
        var cut = ctx.Render<ProviderEditDialog>(parameters => parameters
            .Add(p => p.IsNew, false)
            .Add(p => p.Key, "ollama")
            .Add(p => p.BaseUrl, "http://localhost:11434/v1")
            .Add(p => p.HasApiKey, false));

        cut.Find("[data-testid='requires-auth']").HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public void Unticking_requires_authentication_emits_none_and_clears_credentials()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            // HasApiKey: true opens with a stored literal credential; the user then switches auth off.
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.Find("[data-testid='requires-auth']").Change(false);
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.CredentialMode.Should().Be(ProviderCredentialModes.None);
        saved.ApiKey.Should().BeNull();
        saved.ApiKeyEnvVar.Should().BeNull();
    }

    [Fact]
    public void A_bare_environment_variable_name_saves_with_no_scheme()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.Find("[data-testid='credential-type-0']").Change("env");
        cut.Find("[data-testid='credential-value-0']").Input("MY_KEY");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.CredentialMode.Should().Be(ProviderCredentialModes.EnvVar);
        saved.ApiKeyEnvVar.Should().Be("MY_KEY");
        saved.ApiKey.Should().BeNull();
        saved.AuthHeaderScheme.Should().BeEmpty();
    }

    [Fact]
    public void A_bearer_value_template_is_decomposed_into_scheme_and_variable_name()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.Find("[data-testid='credential-header-0']").Input("Authorization");
        cut.Find("[data-testid='credential-type-0']").Change("env");
        cut.Find("[data-testid='credential-value-0']").Input("Bearer {env:OPENAI_API_KEY}");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.AuthHeaderName.Should().Be("Authorization");
        saved.AuthHeaderScheme.Should().Be("Bearer");
        saved.ApiKeyEnvVar.Should().Be("OPENAI_API_KEY");
        saved.ApiKey.Should().BeNull();
    }

    [Fact]
    public void An_existing_env_var_credential_reopens_as_its_value_template()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => parameters
            .Add(p => p.IsNew, false)
            .Add(p => p.Key, "openai")
            .Add(p => p.BaseUrl, "https://api.openai.com/v1")
            .Add(p => p.AuthHeaderName, "Authorization")
            .Add(p => p.AuthHeaderScheme, "Bearer")
            .Add(p => p.ApiKeyEnvVar, "OPENAI_API_KEY")
            .Add(p => p.HasApiKey, false));

        cut.Find("[data-testid='credential-value-0']").GetAttribute("value")
            .Should().Be("Bearer {env:OPENAI_API_KEY}");
    }

    [Fact]
    public void Keeping_a_stored_literal_key_preserves_its_scheme()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            // A Bearer-scheme provider with a stored literal key. The key is never returned, so the box is
            // blank; saving untouched must keep BOTH the key and the scheme, or the preserved key would go
            // upstream raw and 401.
            parameters
                .Add(p => p.IsNew, false)
                .Add(p => p.Key, "openai")
                .Add(p => p.BaseUrl, "https://api.openai.com/v1")
                .Add(p => p.AuthHeaderName, "Authorization")
                .Add(p => p.AuthHeaderScheme, "Bearer")
                .Add(p => p.HasApiKey, true)
                .Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.CredentialMode.Should().Be(ProviderCredentialModes.Literal);
        saved.ApiKey.Should().BeNull();
        saved.AuthHeaderScheme.Should().Be("Bearer");
    }

    [Fact]
    public void A_new_literal_credential_carries_its_own_prefix_and_clears_the_scheme()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            parameters
                .Add(p => p.IsNew, false)
                .Add(p => p.Key, "openai")
                .Add(p => p.BaseUrl, "https://api.openai.com/v1")
                .Add(p => p.AuthHeaderName, "Authorization")
                .Add(p => p.AuthHeaderScheme, "Bearer")
                .Add(p => p.HasApiKey, true)
                .Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        // A literal is sent verbatim, so it carries its own prefix and the stored scheme must be dropped -
        // otherwise the header would read "Bearer Bearer sk-...".
        cut.Find("[data-testid='credential-value-0']").Input("Bearer sk-new");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.ApiKey.Should().Be("Bearer sk-new");
        saved.AuthHeaderScheme.Should().BeEmpty();
    }

    [Fact]
    public void Selecting_anthropic_seeds_its_header_suggestion_and_required_version_header()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => parameters
            .Add(p => p.IsNew, true)
            .Add(p => p.HasApiKey, false));

        cut.Find("[data-testid='provider-type']").Change(nameof(ProviderType.Anthropic));

        cut.Find("[data-testid='base-url']").GetAttribute("value").Should().Be("https://api.anthropic.com");
        cut.Find("[data-testid='credential-header-0']").GetAttribute("value").Should().Be("x-api-key");
        // The greyed suggestion is placeholder text, never a pre-filled value.
        cut.Find("[data-testid='credential-value-0']").GetAttribute("placeholder").Should().Be("ANTHROPIC_API_KEY");
        cut.Find("[data-testid='credential-value-0']").GetAttribute("value").Should().BeNullOrEmpty();
        // Anthropic's API rejects every request without this header, so the template supplies it.
        cut.Markup.Should().Contain("anthropic-version");
    }

    [Fact]
    public void Selecting_openai_suggests_the_bearer_value_template()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => parameters
            .Add(p => p.IsNew, true)
            .Add(p => p.HasApiKey, false));

        cut.Find("[data-testid='provider-type']").Change(nameof(ProviderType.OpenAI));

        cut.Find("[data-testid='credential-header-0']").GetAttribute("value").Should().Be("Authorization");
        cut.Find("[data-testid='credential-value-0']").GetAttribute("placeholder")
            .Should().Be("Bearer {env:OPENAI_API_KEY}");
    }

    [Fact]
    public void Selecting_a_local_runtime_unticks_requires_authentication()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => parameters
            .Add(p => p.IsNew, true)
            .Add(p => p.HasApiKey, false));

        cut.Find("[data-testid='provider-type']").Change(nameof(ProviderType.LocalRuntime));

        cut.Find("[data-testid='requires-auth']").HasAttribute("checked").Should().BeFalse();
        // A local runtime costs nothing, so its models report a known $0.00 rather than an unknown cost.
        cut.Find("[data-testid='is-free']").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void The_selected_provider_type_is_emitted_on_save()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.Find("[data-testid='provider-type']").Change(nameof(ProviderType.Anthropic));
        cut.Find("[data-testid='credential-value-0']").Input("ANTHROPIC_API_KEY");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.ProviderType.Should().Be(nameof(ProviderType.Anthropic));
    }

    [Fact]
    public void An_existing_provider_type_is_preselected_when_the_dialog_opens()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.ProviderType, nameof(ProviderType.Anthropic));
        });

        // Guards the round-trip bug: ProvidersAdmin used to hardcode "Other", so an Anthropic provider
        // always reopened as Other and lost its type on the next save.
        cut.Find("[data-testid='provider-type']").GetAttribute("value").Should().Be(nameof(ProviderType.Anthropic));
    }

    [Fact]
    public void A_second_credential_is_emitted_as_a_custom_header()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.Find("[data-testid='add-credential']").Click();
        cut.Find("[data-testid='credential-header-1']").Input("Ocp-Apim-Subscription-Key");
        cut.Find("[data-testid='credential-value-1']").Input("APIM_SUBSCRIPTION_KEY");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        // Credentials past the first have no dedicated storage; they ride the existing custom-header list.
        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Name.Should().Be("Ocp-Apim-Subscription-Key");
        header.ValueEnvVar.Should().Be("APIM_SUBSCRIPTION_KEY");
        header.Value.Should().BeNull();
    }

    [Fact]
    public void A_prefix_on_a_second_credential_is_rejected_with_a_reason()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(SeedEditParameters);

        cut.Find("[data-testid='add-credential']").Click();
        cut.Find("[data-testid='credential-header-1']").Input("X-Extra");
        cut.Find("[data-testid='credential-value-1']").Input("Bearer {env:EXTRA_KEY}");

        // Custom headers have no scheme field, so this cannot be stored - and a disabled Save button must
        // always come with a stated reason rather than leaving the user guessing.
        cut.Find("[data-testid='dialog-error']").TextContent.Should().Contain("Only the first credential");
        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void A_blank_credential_value_on_a_new_provider_blocks_save_with_a_reason()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => parameters
            .Add(p => p.IsNew, true)
            .Add(p => p.HasApiKey, false));

        cut.Find("[data-testid='provider-name']").Input("My Provider");
        cut.Find("[data-testid='provider-type']").Change(nameof(ProviderType.Anthropic));

        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid='dialog-error']").TextContent.Should().Contain("environment variable name");
    }

    [Fact]
    public void Adding_a_literal_custom_header_is_emitted_on_save()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Add header")).Click();
        cut.Find("input[placeholder='Header-Name']").Input("X-Test");
        cut.Find("input[placeholder='value']").Input("hello");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Name.Should().Be("X-Test");
        header.Value.Should().Be("hello");
        header.ValueEnvVar.Should().BeNull();
    }

    [Fact]
    public void Adding_an_env_var_custom_header_is_emitted_as_an_env_reference()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Add header")).Click();
        cut.Find("input[placeholder='Header-Name']").Input("X-Secret");
        // Switch the row's value source to an environment variable (the header section's select is the last one).
        cut.FindAll("select").Last().Change("env");
        cut.Find("input[placeholder='ENV_VAR_NAME']").Input("MY_SECRET_VAR");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Name.Should().Be("X-Secret");
        header.ValueEnvVar.Should().Be("MY_SECRET_VAR");
        header.Value.Should().BeNull();
    }

    [Fact]
    public void Existing_literal_header_shows_saved_indicator_and_round_trips_blank_to_preserve_it()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            // The management API is write-only for header secrets: a GET never carries the literal value,
            // only that one is set (HeaderValueSource.Literal).
            parameters.Add(p => p.Headers, new[] { new ProviderHeaderView("anthropic-version", HeaderValueSource.Literal, null) });
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        // The existing header is shown in the editor, with a "saved" placeholder rather than the value...
        cut.Markup.Should().Contain("anthropic-version");
        cut.Markup.Should().Contain("saved, blank keeps it");

        // ...and saving without re-entering it sends it blank - the server preserves the stored value.
        FindSaveButton(cut).Click();

        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Name.Should().Be("anthropic-version");
        header.Value.Should().BeNullOrEmpty();
        header.ValueEnvVar.Should().BeNull();
    }

    private static void SeedEditParameters(ComponentParameterCollectionBuilder<ProviderEditDialog> parameters) =>
        parameters
            .Add(p => p.IsNew, false)
            .Add(p => p.Key, Key)
            .Add(p => p.BaseUrl, OriginalBaseUrl)
            .Add(p => p.AuthHeaderName, "x-api-key")
            .Add(p => p.AuthHeaderScheme, string.Empty)
            .Add(p => p.HasApiKey, true);

    private static AngleSharp.Dom.IElement FindSaveButton(IRenderedComponent<ProviderEditDialog> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save");
}
