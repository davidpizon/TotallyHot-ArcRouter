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
/// <c>FindAll("select")[1]</c> lookups broke the moment a field was added above them.
/// </para>
/// </summary>
public sealed class ProviderEditDialogTests
{
    private const string Key = "anthropic";
    private const string OriginalBaseUrl = "https://api.anthropic.com";
    private const string EditedBaseUrl = "https://api.anthropic.com/edited";

    /// <summary>A stored header the operator marked secret: the router withholds its value, so the view
    /// carries the source alone.</summary>
    private static readonly ProviderHeaderView LockedHeader =
        new("X-Subscription-Key", HeaderValueSource.Literal, null, Value: null, Locked: true);

    /// <summary>A stored header left unlocked - ordinary public configuration, returned in full.</summary>
    private static readonly ProviderHeaderView UnlockedHeader =
        new("anthropic-version", HeaderValueSource.Literal, null, Value: "2023-06-01", Locked: false);

    [Fact]
    public void Renders_the_real_provider_key_in_the_edit_title()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(SeedEditParameters);

        // Guards the symptom that the title showed a literal placeholder ("EDIT _DIALOGKEY") rather than
        // the provider being edited.
        cut.Markup.Should().Contain($"Edit {Key}");
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
    public void Save_always_clears_the_legacy_credential_fields()
    {
        using var ctx = new Bunit.BunitContext();

        // Authentication is now expressed purely via Custom Headers; the dialog no longer has a way to
        // author a legacy literal/env-var credential, so Save must always clear it (CredentialMode: None).
        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.CredentialMode.Should().Be(ProviderCredentialModes.None);
        saved.ApiKey.Should().BeNull();
        saved.ApiKeyEnvVar.Should().BeNull();
        saved.AuthHeaderScheme.Should().BeEmpty();
        // No ProviderType parameter is seeded, so the dialog falls back to Other, whose template names
        // "Authorization" - not the stale "x-api-key" the AuthHeaderName parameter was seeded with. See
        // Save_derives_auth_header_name_from_the_selected_provider_type for the templated case.
        saved.AuthHeaderName.Should().Be("Authorization");
    }

    [Fact]
    public void Save_derives_auth_header_name_from_the_selected_provider_type()
    {
        using var ctx = new Bunit.BunitContext();

        // AuthHeaderName must track the header the operator actually authenticates with, not a value left
        // over from before this dialog stopped letting them edit it directly - so switching provider type to
        // one with a documented auth header (Anthropic's x-api-key) must override whatever the AuthHeaderName
        // parameter (or, absent a selected type, its template) would otherwise have produced.
        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.Find("[data-testid='provider-type']").Change(nameof(ProviderType.Anthropic));
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.AuthHeaderName.Should().Be("x-api-key");
    }

    [Fact]
    public void Save_migrates_a_legacy_api_key_into_a_custom_header()
    {
        using var ctx = new Bunit.BunitContext();

        // Because Save always clears the legacy ApiKey/CredentialMode (see the test above), a provider
        // that still had one stored under the old model would otherwise lose it silently the first time it
        // was opened and saved through this dialog. The dialog must migrate it into a same-named custom
        // header instead.
        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters, hasApiKey: true);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        // The literal key is never sent to this dialog, so the migrated row shows as a locked, blank,
        // "saved" placeholder - exactly like any other pre-existing locked header.
        var value = cut.Find("[data-testid='header-value-0']");
        value.GetAttribute("type").Should().Be("password");
        value.GetAttribute("placeholder").Should().Contain("saved, blank keeps it");

        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.CredentialMode.Should().Be(ProviderCredentialModes.None);
        saved.ApiKey.Should().BeNull();
        var header = saved.Headers.Should().ContainSingle().Subject;
        header.Name.Should().Be("x-api-key");
        header.Locked.Should().BeTrue();
        // Blank + locked tells the server to preserve whatever is already stored under this header's name
        // (here, the legacy ApiKey) rather than clearing it - see ManagementFacade.ResolveHeader.
        header.Value.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Save_migrates_a_legacy_env_var_credential_into_a_custom_header()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.ApiKeyEnvVar, "MY_API_KEY");
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        var header = saved!.Headers.Should().ContainSingle().Subject;
        header.Name.Should().Be("x-api-key");
        header.ValueEnvVar.Should().Be("MY_API_KEY");
        header.Value.Should().BeNull();
    }

    [Fact]
    public void An_existing_custom_header_under_the_auth_header_name_is_not_duplicated_by_migration()
    {
        using var ctx = new Bunit.BunitContext();

        // The provider already has its auth expressed as a custom header (the common, already-migrated
        // case) - SeedLegacyCredentialHeader must leave it alone rather than adding a second row.
        var existingAuthHeader = new ProviderHeaderView("x-api-key", HeaderValueSource.Literal, null, Value: "already-set", Locked: false);
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters, hasApiKey: true);
            parameters.Add(p => p.Headers, new[] { existingAuthHeader });
        });

        cut.FindAll("[data-testid^='header-name-']").Should().ContainSingle();
    }

    [Fact]
    public void Selecting_anthropic_seeds_the_base_url_and_required_version_header()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => parameters
            .Add(p => p.IsNew, true)
            .Add(p => p.HasApiKey, false));

        cut.Find("[data-testid='provider-type']").Change(nameof(ProviderType.Anthropic));

        cut.Find("[data-testid='base-url']").GetAttribute("value").Should().Be("https://api.anthropic.com");
        // Anthropic's API rejects every request without this header, so the template supplies it.
        cut.Markup.Should().Contain("anthropic-version");
    }

    [Fact]
    public void Selecting_a_local_runtime_flags_the_provider_as_free()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters => parameters
            .Add(p => p.IsNew, true)
            .Add(p => p.HasApiKey, false));

        cut.Find("[data-testid='provider-type']").Change(nameof(ProviderType.LocalRuntime));

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
    public void Adding_a_literal_custom_header_is_emitted_on_save()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
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
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
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
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            // A locked header's literal value is never carried by a GET, only the fact that one is set.
            parameters.Add(p => p.Headers, new[] { LockedHeader });
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
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
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.Headers, new[] { UnlockedHeader });
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
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
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.Headers, new[] { UnlockedHeader });
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
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
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.Headers, new[] { LockedHeader });
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
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
    public void An_env_var_header_has_no_padlock()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.Headers, new[] { new ProviderHeaderView("X-Secret", HeaderValueSource.EnvVar, "MY_SECRET_VAR") });
        });

        // An env-var header names a variable; the secret lives outside the config file, so there is
        // nothing for a lock to withhold.
        cut.FindAll("[data-testid='header-value-0-lock']").Should().BeEmpty();
        cut.Find("[data-testid='header-value-0']").GetAttribute("value").Should().Be("MY_SECRET_VAR");
    }

    [Fact]
    public void Switching_a_locked_header_to_an_env_var_drops_its_lock()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.Headers, new[] { LockedHeader });
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
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
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(SeedEditParameters);

        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='header-name-0']").Input("Authorization");
        cut.Find("[data-testid='header-name-1']").Input("Authorization");

        cut.Find("[data-testid='header-duplicate-0']").TextContent.Should().Be("Duplicate header field");
        cut.Find("[data-testid='header-duplicate-1']").TextContent.Should().Be("Duplicate header field");
        cut.Find("[data-testid='dialog-error']").TextContent.Should().Contain("Duplicate header field");
        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Duplicate_header_detection_is_case_insensitive()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(SeedEditParameters);

        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='add-header']").Click();
        cut.Find("[data-testid='header-name-0']").Input("Authorization");
        cut.Find("[data-testid='header-name-1']").Input("authorization");

        cut.Find("[data-testid='dialog-error']").TextContent.Should().Contain("Duplicate header field");
        FindSaveButton(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Renaming_a_duplicate_header_re_enables_save()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<ProviderEditDialog>(SeedEditParameters);

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

    private static void SeedEditParameters(ComponentParameterCollectionBuilder<ProviderEditDialog> parameters) =>
        SeedEditParameters(parameters, hasApiKey: false);

    private static void SeedEditParameters(ComponentParameterCollectionBuilder<ProviderEditDialog> parameters, bool hasApiKey) =>
        parameters
            .Add(p => p.IsNew, false)
            .Add(p => p.Key, Key)
            .Add(p => p.BaseUrl, OriginalBaseUrl)
            .Add(p => p.AuthHeaderName, "x-api-key")
            .Add(p => p.AuthHeaderScheme, string.Empty)
            // No legacy credential by default, so tests that don't care about it aren't tripped up by the
            // legacy-credential-migration row that SeedLegacyCredentialHeader adds when HasApiKey is set
            // (see Save_migrates_a_legacy_api_key_into_a_custom_header for that behavior specifically).
            .Add(p => p.HasApiKey, hasApiKey);

    private static AngleSharp.Dom.IElement FindSaveButton(IRenderedComponent<ProviderEditDialog> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save");
}
