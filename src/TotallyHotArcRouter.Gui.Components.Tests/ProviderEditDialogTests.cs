using AwesomeAssertions;
using Bunit;
using TotallyHot.ArcRouter.Gui.Admin;
using TotallyHot.ArcRouter.Gui.Components;
using IElement = AngleSharp.Dom.IElement;

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
/// <c>FindAll("select")[1]</c> lookups broke the moment a field was added above them.
/// </para>
/// </summary>
public sealed class ProviderEditDialogTests
{
    private const string Key = "anthropic";
    private const string OriginalBaseUrl = "https://api.anthropic.com";
    private const string EditedBaseUrl = "https://api.anthropic.com/edited";

    /// <summary>
    /// A stored header the operator marked secret: the router withholds its value, so the view
    /// carries the source alone.
    /// </summary>
    private static readonly ProviderHeaderView LockedHeader =
        new(Name: "X-Subscription-Key", Source: HeaderValueSource.Literal, null, null, true);

    /// <summary>A stored header left unlocked - ordinary public configuration, returned in full.</summary>
    private static readonly ProviderHeaderView UnlockedHeader =
        new(Name: "anthropic-version", Source: HeaderValueSource.Literal, null, Value: "2023-06-01");

    [Fact]
    public void Renders_a_static_title_when_editing()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => SeedEditParameters(parameters));

        cut.Markup.Should().Contain("Edit Provider");
    }

    [Fact]
    public void Edit_survives_parent_rerender_before_save()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        // The user edits the Base URL (the component binds @oninput, so dispatch an input event).
        cut.Find("[data-testid='base-url']").Input(EditedBaseUrl);

        // A live-telemetry tick re-renders the parent, which re-supplies the dialog's ORIGINAL parameters.
        // This is the exact event that used to clobber the in-progress edit.
        cut.Render(parameters => SeedEditParameters(parameters));

        // The user clicks Save.
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.Key.Should().Be(Key);
        saved.BaseUrl.Should().Be(EditedBaseUrl);
    }

    [Fact]
    public void Save_sends_the_edited_value_without_an_intervening_rerender()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='base-url']").Input(EditedBaseUrl);
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.BaseUrl.Should().Be(EditedBaseUrl);
    }

    [Fact]
    public void Free_provider_checkbox_seeds_from_the_parameter()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
            SeedEditParameters(parameters: parameters, isFree: true));

        cut.Find("[data-testid='is-free']").HasAttribute("checked").Should().BeTrue();
    }

    // The flag rides the same OnInitialized-only seeding as every other field, so it must survive a
    // parent re-render mid-edit for the same reason (see this class's summary).
    [Fact]
    public void Ticking_free_provider_emits_it_on_save()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='is-free']").Change(true);
        cut.Render(parameters => SeedEditParameters(parameters));
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.IsFree.Should().BeTrue();
    }

    [Fact]
    public void Save_adds_no_implicit_headers_when_the_provider_type_has_no_template()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        // With no template to declare one, no header (and so no lock) appears on its own: only a template's
        // declaration or the operator's own padlock ever locks a row.
        saved!.Headers.Should().BeEmpty();
    }

    [Fact]
    public void A_template_credential_row_starts_on_its_env_var_and_saves_unlocked()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, isNew: true, providerName: "OpenAI");
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='provider-type']").Change("openai");

        cut.Find("[data-testid='header-name-0']").GetAttribute("value").Should().Be("Authorization");
        cut.Find("[data-testid='header-source-0']").GetAttribute("value").Should().Be("env");
        cut.Find("[data-testid='header-value-0']").GetAttribute("value").Should().Be("OPENAI_API_KEY");

        FindSaveButton(cut).Click();

        // An env-var row holds only a variable name, so it is never stored locked.
        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.ValueEnvVar.Should().Be("OPENAI_API_KEY");
        header.Locked.Should().BeFalse();
    }

    [Fact]
    public void Switching_a_template_credential_row_to_a_literal_locks_it()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, isNew: true, providerName: "OpenAI");
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='provider-type']").Change("openai");
        cut.Find("[data-testid='header-source-0']").Change("literal");
        cut.Find("[data-testid='header-value-0']").Input("sk-typed-by-the-operator");
        FindSaveButton(cut).Click();

        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Value.Should().Be("sk-typed-by-the-operator");
        header.Locked.Should().BeTrue();
    }

    [Fact]
    public void Reopening_a_provider_and_switching_its_env_var_credential_to_a_literal_still_locks_it()
    {
        using var ctx = new BunitContext();

        // The rows of a stored provider come from stored state, not from the template, so nothing was
        // copied onto them when the dialog opened - the template's declaration has to be looked up.
        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(
                parameters: parameters,
                headers:
                [
                    new ProviderHeaderView(Name: "Authorization", Source: HeaderValueSource.EnvVar,
                        ValueEnvVar: "OPENAI_API_KEY")
                ],
                providerType: "openai");
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='header-source-0']").Change("literal");
        cut.Find("[data-testid='header-value-0']").Input("sk-typed-after-reopening");
        FindSaveButton(cut).Click();

        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Value.Should().Be("sk-typed-after-reopening");
        header.Locked.Should().BeTrue();
    }

    [Fact]
    public void A_template_that_declares_no_credential_adds_no_header_rows()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
            SeedEditParameters(parameters: parameters, isNew: true));

        cut.Find("[data-testid='provider-type']").Change("ollama");

        cut.FindAll("[data-testid^='header-name-']").Should().BeEmpty();
    }

    [Fact]
    public void Selecting_anthropic_seeds_the_base_url_and_required_version_header()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => SeedEditParameters(parameters: parameters, true));

        cut.Find("[data-testid='provider-type']").Change("anthropic");

        cut.Find("[data-testid='base-url']").GetAttribute("value").Should().Be("https://api.anthropic.com");
        // Anthropic's API rejects every request without this header, so the template supplies it.
        cut.Markup.Should().Contain("anthropic-version");
    }

    [Fact]
    public void Selecting_a_local_runtime_flags_the_provider_as_free()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => SeedEditParameters(parameters: parameters, true));

        cut.Find("[data-testid='provider-type']").Change("ollama");

        // A local runtime costs nothing, so its models report a known $0.00 rather than an unknown cost.
        cut.Find("[data-testid='is-free']").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void The_selected_provider_type_is_emitted_on_save()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='provider-type']").Change("anthropic");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.ProviderType.Should().Be("anthropic");
    }

    [Fact]
    public void A_failed_template_catalog_does_not_rewrite_the_stored_provider_type_on_save()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            parameters.Add(parameterSelector: p => p.Templates,
                value: (IReadOnlyList<ProviderTemplates.ProviderEditorTemplate>)[]);
            parameters.Add(parameterSelector: p => p.TemplatesUnavailable, value: true);
            parameters.Add(parameterSelector: p => p.Model, value: new ProviderEditDialog.ProviderEditModel(
                Key: Key,
                IsNew: false,
                BaseUrl: OriginalBaseUrl,
                Headers: [],
                IsFree: false,
                ProviderType: "anthropic",
                ProviderName: "Anthropic"));
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.ProviderType.Should().Be("anthropic");
    }

    [Fact]
    public void An_unknown_stored_type_is_kept_when_the_selection_stays_other()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, providerType: "Bedrock", providerName: "Bedrock");
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.ProviderType.Should().Be("Bedrock");
    }

    [Fact]
    public void Choosing_a_template_replaces_a_preserved_stored_type()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, providerType: "Bedrock", providerName: "Bedrock");
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='provider-type']").Change("anthropic");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.ProviderType.Should().Be("anthropic");
    }

    [Fact]
    public void An_existing_provider_type_is_preselected_when_the_dialog_opens()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
            SeedEditParameters(parameters: parameters, providerType: nameof(ProviderType.Anthropic)));

        // Guards the round-trip bug: ProvidersAdmin used to hardcode "Other", so an Anthropic provider
        // always reopened as Other and lost its type on the next save.
        cut.Find("[data-testid='provider-type']").GetAttribute("value").Should().Be("anthropic");
    }

    [Fact]
    public void Adding_a_literal_custom_header_is_emitted_on_save()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='add-header']").Click();
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
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='add-header']").Click();
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
    public void Existing_locked_header_hides_its_value_and_round_trips_blank_to_preserve_it()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            // A locked header's literal value is never carried by a GET, only the fact that one is set.
            SeedEditParameters(parameters: parameters, headers: [LockedHeader]);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        // The existing header is shown in the editor, masked, with a "saved" placeholder rather than the
        // value...
        cut.Markup.Should().Contain("X-Subscription-Key");
        var value = cut.Find("[data-testid='header-value-0']");
        value.GetAttribute("type").Should().Be("password");
        value.GetAttribute("value").Should().BeNullOrEmpty();
        value.GetAttribute("placeholder").Should().Contain("saved, blank keeps it");

        // ...and saving without re-entering it sends it blank but still locked - which is what tells the
        // server to preserve the stored value rather than clear it.
        FindSaveButton(cut).Click();

        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Name.Should().Be("X-Subscription-Key");
        header.Value.Should().BeNullOrEmpty();
        header.ValueEnvVar.Should().BeNull();
        header.Locked.Should().BeTrue();
    }

    [Fact]
    public void Unlocked_header_value_is_shown_in_full_and_saved_as_typed()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, headers: [UnlockedHeader]);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        // The point of the unlocked default: public configuration is readable and directly editable.
        var value = cut.Find("[data-testid='header-value-0']");
        value.GetAttribute("type").Should().Be("text");
        value.GetAttribute("value").Should().Be("2023-06-01");

        value.Input("2024-01-01");
        FindSaveButton(cut).Click();

        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Value.Should().Be("2024-01-01");
        header.Locked.Should().BeFalse();
    }

    [Fact]
    public void Locking_a_header_keeps_the_typed_value_in_one_click()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, headers: [UnlockedHeader]);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='header-value-0-lock']").Click();

        cut.Find("[data-testid='header-value-0']").GetAttribute("type").Should().Be("password");

        FindSaveButton(cut).Click();

        // Locking costs nothing: the value the operator could already see is sent along with the flag.
        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Value.Should().Be("2023-06-01");
        header.Locked.Should().BeTrue();
    }

    [Fact]
    public void Unlocking_a_header_confirms_via_dialog_and_clears_the_value()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, headers: [LockedHeader]);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        // Clicking the padlock only opens the confirmation dialog - the field is still locked.
        cut.Find("[data-testid='header-value-0-lock']").Click();
        cut.Find("[data-testid='header-value-0']").GetAttribute("type").Should().Be("password");

        cut.Find("[data-testid='header-value-0-continue']").Click();
        var value = cut.Find("[data-testid='header-value-0']");
        value.GetAttribute("type").Should().Be("text");
        value.GetAttribute("value").Should().BeNullOrEmpty();
        // The saved-value placeholder must go too: there is nothing left for a blank field to preserve.
        value.GetAttribute("placeholder").Should().NotContain("saved");

        FindSaveButton(cut).Click();

        // Blank under an explicit unlock is what tells the server to clear the stored secret.
        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Value.Should().BeNullOrEmpty();
        header.Locked.Should().BeFalse();
    }

    [Fact]
    public void A_manually_added_header_is_saved_unlocked_until_the_operator_locks_it()
    {
        using var ctx = new BunitContext();

        // Nothing is locked implicitly (ADR-0016): AddHeader() starts a row unlocked, and only a
        // template's declaration or the operator's own padlock locks it. The management API echoes an
        // unlocked literal back on every read, which is what makes the padlock a real choice.
        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        // With no ProviderType parameter set, the dialog falls back to Other, which adds no rows of its own.
        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='header-name-0']").Input("x-api-key");
        cut.Find("[data-testid='header-value-0']").Input("super-secret-key");

        // The row's own padlock was never touched - it is still showing unlocked.
        cut.Find("[data-testid='header-value-0']").GetAttribute("type").Should().Be("text");

        FindSaveButton(cut).Click();

        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Name.Should().Be("x-api-key");
        header.Value.Should().Be("super-secret-key");
        header.Locked.Should().BeFalse();
    }

    [Fact]
    public void An_env_var_header_has_no_padlock()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters,
                headers:
                [
                    new ProviderHeaderView(Name: "X-Secret", Source: HeaderValueSource.EnvVar,
                        ValueEnvVar: "MY_SECRET_VAR")
                ]);
        });

        // An env-var header names a variable; the secret lives outside the config file, so there is
        // nothing for a lock to withhold.
        cut.FindAll("[data-testid='header-value-0-lock']").Should().BeEmpty();
        cut.Find("[data-testid='header-value-0']").GetAttribute("value").Should().Be("MY_SECRET_VAR");
    }

    [Fact]
    public void Switching_a_locked_header_to_an_env_var_drops_its_lock()
    {
        using var ctx = new BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, headers: [LockedHeader]);
            parameters.Add(parameterSelector: p => p.OnSave, callback: r => saved = r);
        });

        cut.Find("[data-testid='header-source-0']").Change("env");
        cut.Find("[data-testid='header-value-0']").Input("MY_SECRET_VAR");
        FindSaveButton(cut).Click();

        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.ValueEnvVar.Should().Be("MY_SECRET_VAR");
        header.Locked.Should().BeFalse();
    }

    [Fact]
    public void Two_headers_with_the_same_name_are_flagged_as_duplicate_and_block_save()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => SeedEditParameters(parameters));

        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='header-name-0']").Input("Authorization");
        cut.Find("[data-testid='header-name-1']").Input("Authorization");

        cut.Find("[data-testid='header-duplicate-0']").TextContent.Should().Be("Duplicate header name: Authorization");
        cut.Find("[data-testid='header-duplicate-1']").TextContent.Should().Be("Duplicate header name: Authorization");
        cut.Find("[data-testid='dialog-error']").TextContent.Should().Contain("Duplicate header name: Authorization");
        cut.Find("[data-testid='header-name-0']").GetAttribute("aria-invalid").Should().Be("true");
        cut.Find("[data-testid='header-name-0']").GetAttribute("aria-describedby").Should().Be("header-duplicate-0");
        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Duplicate_header_detection_is_case_insensitive()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => SeedEditParameters(parameters));

        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='header-name-0']").Input("Authorization");
        cut.Find("[data-testid='header-name-1']").Input("authorization");

        cut.Find("[data-testid='dialog-error']").TextContent.Should().Contain("Duplicate header name:");
        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Renaming_a_duplicate_header_re_enables_save()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => SeedEditParameters(parameters));

        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='header-name-0']").Input("Authorization");
        cut.Find("[data-testid='header-name-1']").Input("Authorization");
        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();

        cut.Find("[data-testid='header-name-1']").Input("X-Other");

        cut.FindAll("[data-testid='header-duplicate-0']").Should().BeEmpty();
        cut.FindAll("[data-testid='header-duplicate-1']").Should().BeEmpty();
        FindSaveButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void A_new_providers_name_colliding_with_an_existing_provider_blocks_save()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, isNew: true, key: string.Empty, baseUrl: string.Empty);
            parameters.Add(parameterSelector: p => p.ExistingProviderNames, value: ["OpenAI API"]);
        });

        cut.Find("[data-testid='provider-name']").Input("OpenAI API");
        cut.Find("[data-testid='base-url']").Input("https://api.example.com");

        cut.Find("[data-testid='dialog-error']").TextContent
            .Should().Contain("Provider name 'OpenAI API' is already in use by another provider.");
        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Name_collision_detection_is_case_insensitive_and_trims_whitespace()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, isNew: true, key: string.Empty, baseUrl: string.Empty);
            parameters.Add(parameterSelector: p => p.ExistingProviderNames, value: ["OpenAI API"]);
        });

        cut.Find("[data-testid='provider-name']").Input("  openai api  ");
        cut.Find("[data-testid='base-url']").Input("https://api.example.com");

        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Renaming_away_from_a_collision_re_enables_save()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, isNew: true, key: string.Empty, baseUrl: string.Empty);
            parameters.Add(parameterSelector: p => p.ExistingProviderNames, value: ["OpenAI API"]);
        });

        cut.Find("[data-testid='provider-name']").Input("OpenAI API");
        cut.Find("[data-testid='base-url']").Input("https://api.example.com");
        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();

        cut.Find("[data-testid='provider-name']").Input("My OpenAI Instance");

        cut.FindAll("[data-testid='dialog-error']").Should().BeEmpty();
        FindSaveButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Editing_a_provider_without_changing_its_own_name_does_not_block_save()
    {
        using var ctx = new BunitContext();

        // The edited provider's own current name is excluded from ExistingProviderNames by the caller
        // (ProvidersAdmin), so keeping it unchanged must not trip the collision check.
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters: parameters, isNew: false, providerName: "OpenAI API");
            parameters.Add(parameterSelector: p => p.ExistingProviderNames, value: []);
        });

        cut.FindAll("[data-testid='dialog-error']").Should().BeEmpty();
        FindSaveButton(cut).HasAttribute("disabled").Should().BeFalse();
    }

    private static void SeedEditParameters(
        ComponentParameterCollectionBuilder<ProviderEditDialog> parameters,
        bool isNew = false,
        string key = Key,
        string baseUrl = OriginalBaseUrl,
        string authHeaderName = "x-api-key",
        IReadOnlyList<ProviderHeaderView>? headers = null,
        bool isFree = false,
        string providerType = "",
        string providerName = "")
    {
        parameters.Add(parameterSelector: p => p.Templates, value: SampleTemplates());
        parameters.Add(parameterSelector: p => p.Model, value: new ProviderEditDialog.ProviderEditModel(
            Key: key,
            IsNew: isNew,
            BaseUrl: baseUrl,
            Headers: headers ?? [],
            IsFree: isFree,
            ProviderType: providerType,
            ProviderName: providerName));
    }

    [Fact]
    public void Switching_templates_without_edits_replaces_custom_headers()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => SeedEditParameters(parameters: parameters, true));

        cut.Find("[data-testid='provider-type']").Change("anthropic");
        cut.Markup.Should().Contain("anthropic-version");

        cut.Find("[data-testid='provider-type']").Change("openai");

        cut.Find("[data-testid='base-url']").GetAttribute("value").Should().Be("https://api.openai.com");
        cut.Markup.Should().NotContain("anthropic-version");
    }

    [Fact]
    public void Switching_templates_after_a_base_url_edit_keeps_custom_headers()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => SeedEditParameters(parameters: parameters, true));

        cut.Find("[data-testid='provider-type']").Change("anthropic");
        cut.Find("[data-testid='base-url']").Input("https://api.anthropic.com/custom");
        cut.Find("[data-testid='provider-type']").Change("openai");

        cut.Find("[data-testid='base-url']").GetAttribute("value").Should().Be("https://api.openai.com");
        cut.Markup.Should().Contain("anthropic-version");
    }

    [Fact]
    public void Selecting_other_without_edits_clears_template_headers()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => SeedEditParameters(parameters: parameters, true));

        cut.Find("[data-testid='provider-type']").Change("anthropic");
        cut.Find("[data-testid='provider-type']").Change(ProviderTemplates.OtherKey);

        cut.Find("[data-testid='base-url']").GetAttribute("value").Should().BeNullOrEmpty();
        cut.Markup.Should().NotContain("anthropic-version");
    }

    private static IReadOnlyList<ProviderTemplates.ProviderEditorTemplate> SampleTemplates()
    {
        return
        [
            new(Key: "anthropic", BaseUrl: "https://api.anthropic.com", IsFree: false,
                Headers: [new ProviderTemplates.ProviderTemplateHeader(Name: "anthropic-version", Value: "2023-06-01")]),
            new(Key: "openai", BaseUrl: "https://api.openai.com", IsFree: false,
                Headers:
                [
                    new ProviderTemplates.ProviderTemplateHeader(Name: "Authorization", Value: null,
                        ValueEnvVar: "OPENAI_API_KEY", Locked: true)
                ]),
            new(Key: "ollama", BaseUrl: "http://localhost:11434/v1", IsFree: true,
                Headers: [])
        ];
    }

    private static IElement FindSaveButton(IRenderedComponent<ProviderEditDialog> cut)
    {
        return cut.FindAll("button").First(b => b.TextContent.Trim() == "Save");
    }
}