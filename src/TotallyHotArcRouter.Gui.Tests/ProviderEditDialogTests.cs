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
/// </summary>
public sealed class ProviderEditDialogTests
{
    private const string Key = "anthropic";
    private const string OriginalBaseUrl = "https://api.anthropic.com";
    private const string EditedBaseUrl = "https://api.anthropic.com/edited";

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
        cut.Find("input[placeholder='https://api.example.com']").Input(EditedBaseUrl);

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

        cut.Find("input[placeholder='https://api.example.com']").Input(EditedBaseUrl);
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.BaseUrl.Should().Be(EditedBaseUrl);
    }

    [Fact]
    public void Editing_a_provider_with_a_stored_key_shows_the_saved_indicator()
    {
        using var ctx = new Bunit.BunitContext();

        // HasApiKey: true (set by SeedEditParameters) opens the dialog in Literal mode for an existing
        // provider. The secret itself is never sent to the client, so the field is blank - the indicator
        // is the only cue a key exists.
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
        cut.Find("input[type='password']").Input("sk-replacement");

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

        cut.Find("input[type='checkbox']").HasAttribute("checked").Should().BeTrue();
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

        cut.Find("input[type='checkbox']").Change(true);
        cut.Render(SeedEditParameters);
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.IsFree.Should().BeTrue();
    }

    [Fact]
    public void Switching_credential_mode_to_none_emits_none_and_clears_credentials()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            // HasApiKey: true opens the dialog in Literal mode; the user then picks "None".
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.FindAll("select")[1].Change(nameof(ProviderEditDialog.CredentialMode.None));
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.CredentialMode.Should().Be(ProviderCredentialModes.None);
        saved.ApiKey.Should().BeNull();
        saved.ApiKeyEnvVar.Should().BeNull();
    }

    [Fact]
    public void Switching_credential_mode_to_env_var_emits_env_var_mode_and_name()
    {
        using var ctx = new Bunit.BunitContext();

        ProviderEditDialog.ProviderEditResult? saved = null;
        var cut = ctx.Render<ProviderEditDialog>(parameters =>
        {
            SeedEditParameters(parameters);
            parameters.Add(p => p.OnSave, (ProviderEditDialog.ProviderEditResult r) => saved = r);
        });

        cut.FindAll("select")[1].Change(nameof(ProviderEditDialog.CredentialMode.EnvVar));
        cut.Find("input[placeholder='OPENAI_API_KEY']").Input("MY_KEY");
        FindSaveButton(cut).Click();

        saved.Should().NotBeNull();
        saved!.CredentialMode.Should().Be(ProviderCredentialModes.EnvVar);
        saved.ApiKeyEnvVar.Should().Be("MY_KEY");
        saved.ApiKey.Should().BeNull();
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

