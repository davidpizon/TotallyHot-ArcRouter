using TotallyHot.ArcRouter.Quality;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The two phase 7 grader-epoch kinds. There is no kind column on <c>grade_receipts</c>.
/// <see cref="GradingRecipes.Bootstrap"/> is non-code. A <c>weights_version</c> equal to the current
/// <see cref="Quality.QualityOptions.WeightsVersion"/> is code. Any other string is neither, because
/// <see cref="GradingRecipes.IsCodeRecipe"/> treats a null recipe id as code and compares to
/// <see cref="GradingRecipes.Code"/>, which is not the string stored in <c>weights_version</c>.
/// </summary>
internal static class GraderEpochKinds
{
    /// <summary>The code-recipe artifact folder and kind string.</summary>
    internal const string Code = "code";

    /// <summary>The non-code artifact folder and kind string.</summary>
    internal const string NonCode = "non-code";

    /// <summary>Both kinds, code first. Each tick visits them independently.</summary>
    internal static readonly string[] Both = [Code, NonCode];

    /// <summary>
    /// The <c>weights_version</c> <see cref="GradeReceiptRecordObserver"/> will store for
    /// <paramref name="recipeId"/>. Bootstrap stays the constant <see cref="GradingRecipes.Bootstrap"/>;
    /// every other recipe, including a null id, is stamped with <paramref name="codeWeightsVersion"/>.
    /// </summary>
    /// <param name="recipeId">The recipe on the quality result.</param>
    /// <param name="codeWeightsVersion">The current <c>QualityOptions.WeightsVersion</c>.</param>
    /// <returns>The string that will be written to <c>grade_receipts.weights_version</c>.</returns>
    internal static string WeightsVersionThatWillBeStored(string? recipeId, string codeWeightsVersion) =>
        string.Equals(recipeId, GradingRecipes.Bootstrap, StringComparison.OrdinalIgnoreCase)
            ? GradingRecipes.Bootstrap
            : codeWeightsVersion;

    /// <summary>
    /// Maps a stored <c>weights_version</c> to a kind. Ordinal, so a future bucket id is not silently
    /// folded into code or non-code.
    /// </summary>
    /// <param name="weightsVersion">The receipt's <c>weights_version</c>.</param>
    /// <param name="codeWeightsVersion">The current code weights version.</param>
    /// <returns><see cref="Code"/>, <see cref="NonCode"/>, or null when the string is neither.</returns>
    internal static string? TryForWeights(string? weightsVersion, string codeWeightsVersion)
    {
        if (string.Equals(weightsVersion, GradingRecipes.Bootstrap, StringComparison.Ordinal))
            return NonCode;
        if (weightsVersion is not null &&
            string.Equals(weightsVersion, codeWeightsVersion, StringComparison.Ordinal))
            return Code;
        return null;
    }

    /// <summary>The <c>weights_version</c> a kind's label query selects.</summary>
    /// <param name="kind"><see cref="Code"/> or <see cref="NonCode"/>.</param>
    /// <param name="codeWeightsVersion">The current code weights version.</param>
    /// <returns>The receipt string for that kind.</returns>
    internal static string WeightsForKind(string kind, string codeWeightsVersion) =>
        kind == NonCode ? GradingRecipes.Bootstrap : codeWeightsVersion;
}
