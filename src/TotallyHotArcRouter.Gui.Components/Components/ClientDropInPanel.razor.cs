using Microsoft.AspNetCore.Components;
using TotallyHot.ArcRouter.Gui.Services;

namespace TotallyHot.ArcRouter.Gui.Components;

/// <summary>
/// Copy-paste card for the default OpenAI-compatible drop-in (one base URL, <c>model: auto</c>).
/// Rendered on the Sessions empty state and at the top of System Settings so a new user does not
/// have to leave the dashboard to point a client. Values come from
/// <see cref="OpenAiCompatibleDropIn"/> — never restated in the markup — so the panel cannot drift
/// from README and GitHub Release notes.
/// </summary>
public partial class ClientDropInPanel
{
    /// <summary>Which copy control last succeeded, so only that button shows its confirmation label.</summary>
    private enum CopiedField
    {
        /// <summary>The Base URL control was copied.</summary>
        BaseUrl,

        /// <summary>The Model control was copied.</summary>
        Model,

        /// <summary>The environment-block control was copied.</summary>
        Env
    }

    private CopiedField? _copied;

    /// <summary>Copies text to the clipboard. The WASM and native hosts each register their own implementation.</summary>
    [Inject]
    public IClipboardService ClipboardService { get; set; } = null!;

    /// <summary>Whether the Base URL copy button should show its confirmation label.</summary>
    private bool BaseUrlCopied => _copied == CopiedField.BaseUrl;

    /// <summary>Whether the Model copy button should show its confirmation label.</summary>
    private bool ModelCopied => _copied == CopiedField.Model;

    /// <summary>Whether the environment-block copy button should show its confirmation label.</summary>
    private bool EnvCopied => _copied == CopiedField.Env;

    /// <summary>Copies the drop-in base URL and marks that button as the one just copied.</summary>
    private Task CopyBaseUrlAsync() => CopyAsync(OpenAiCompatibleDropIn.BaseUrl, CopiedField.BaseUrl);

    /// <summary>Copies <see cref="OpenAiCompatibleDropIn.Model"/> and marks that button as the one just copied.</summary>
    private Task CopyModelAsync() => CopyAsync(OpenAiCompatibleDropIn.Model, CopiedField.Model);

    /// <summary>Copies the two-line environment block and marks that button as the one just copied.</summary>
    private Task CopyEnvAsync() => CopyAsync(OpenAiCompatibleDropIn.BuildEnvironmentExports(), CopiedField.Env);

    /// <summary>Writes <paramref name="text"/> to the clipboard and records which control triggered it.</summary>
    /// <param name="text">Clipboard payload.</param>
    /// <param name="field">Which copy button to flip to its confirmation label.</param>
    private async Task CopyAsync(string text, CopiedField field)
    {
        await ClipboardService.SetTextAsync(text);
        _copied = field;
    }
}
