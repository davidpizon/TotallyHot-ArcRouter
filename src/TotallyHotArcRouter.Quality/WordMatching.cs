namespace TotallyHot.ArcRouter.Quality;

/// <summary>
/// Whole-word substring matching shared by the heuristics in this assembly that scan free text for a
/// fixed token - a keyword, a language name, a branch keyword - without pulling in a regex or a tokenizer.
/// </summary>
/// <remarks>
/// A plain <see cref="string.Contains(string, StringComparison)"/> also matches a needle sitting inside a
/// longer word (e.g. "go" inside "algorithm"), which is wrong for this assembly's purposes. The fix is
/// always the same: scan for every occurrence and check that the characters immediately before and after
/// are not themselves letters or digits.
/// </remarks>
public static class WordMatching
{
    /// <summary>Returns true if <paramref name="needle"/> appears anywhere in <paramref name="haystack"/> as a whole word.</summary>
    /// <param name="haystack">The text to search.</param>
    /// <param name="needle">The token to look for, matched with an ordinal comparison.</param>
    /// <returns><see langword="true"/> if at least one whole-word occurrence exists.</returns>
    public static bool ContainsWholeWord(string haystack, string needle)
    {
        foreach (var _ in WholeWordOccurrences(haystack, needle))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Enumerates the starting index of every occurrence of <paramref name="needle"/> in
    /// <paramref name="haystack"/> that is not immediately preceded or followed by a letter or digit.
    /// </summary>
    /// <param name="haystack">The text to search.</param>
    /// <param name="needle">The non-empty token to look for, matched with an ordinal comparison.</param>
    /// <returns>The zero-based start index of each whole-word match, in order.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="needle"/> is empty. An empty needle would otherwise loop forever: an empty-string
    /// <see cref="string.IndexOf(string, int, StringComparison)"/> match never advances past its own start
    /// index, and every caller of this helper only ever searches for a fixed, non-empty keyword - so a
    /// caller reaching this with an empty needle has a bug worth failing loudly on rather than hanging.
    /// </exception>
    public static IEnumerable<int> WholeWordOccurrences(string haystack, string needle)
    {
        ArgumentException.ThrowIfNullOrEmpty(needle);

        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            var after = index + needle.Length >= haystack.Length || !char.IsLetterOrDigit(haystack[index + needle.Length]);

            if (before && after)
            {
                yield return index;
            }

            index += needle.Length;
        }
    }
}
