using System.Text;

namespace TotallyHot.ArcRouter.Sandbox.Parsing;

/// <summary>A single fenced code block extracted from Markdown-formatted assistant text.</summary>
/// <param name="LanguageHint">The language token following the opening fence (may be empty).</param>
/// <param name="Code">The block's body, newline-normalized.</param>
internal readonly record struct FencedCodeBlock(string LanguageHint, string Code);

/// <summary>Extracts Markdown fenced code blocks (both ``` and ~~~ fences) from assistant text.</summary>
internal static class FencedCodeBlockParser
{
    /// <summary>Parses up to <paramref name="maxBlocks"/> fenced code blocks from <paramref name="text"/>.</summary>
    /// <param name="text">The Markdown-formatted assistant text.</param>
    /// <param name="maxBlocks">The maximum number of blocks to return.</param>
    /// <returns>The extracted blocks, in document order.</returns>
    public static IReadOnlyList<FencedCodeBlock> Parse(string text, int maxBlocks)
    {
        var results = new List<FencedCodeBlock>();
        if (string.IsNullOrEmpty(text) || maxBlocks <= 0)
        {
            return results;
        }

        var lines = text.Split('\n');
        int i = 0;
        while (i < lines.Length && results.Count < maxBlocks)
        {
            var trimmed = lines[i].TrimStart();
            var fence = GetFenceToken(trimmed);
            if (fence is null)
            {
                i++;
                continue;
            }

            var hint = trimmed[fence.Length..].Trim();
            var body = new StringBuilder();
            i++;
            bool closed = false;
            while (i < lines.Length)
            {
                var inner = lines[i];
                if (inner.TrimStart().StartsWith(fence, StringComparison.Ordinal))
                {
                    closed = true;
                    i++;
                    break;
                }

                body.Append(inner.TrimEnd('\r')).Append('\n');
                i++;
            }

            if (closed && body.Length > 0)
            {
                results.Add(new FencedCodeBlock(hint, body.ToString()));
            }
        }

        return results;
    }

    /// <summary>Returns the fence marker (triple backtick or triple tilde) the trimmed line opens with, or null if it is not a fence line.</summary>
    private static string? GetFenceToken(string trimmedLine)
    {
        if (trimmedLine.StartsWith("```", StringComparison.Ordinal))
        {
            return "```";
        }

        if (trimmedLine.StartsWith("~~~", StringComparison.Ordinal))
        {
            return "~~~";
        }

        return null;
    }
}

