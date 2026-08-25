using TotallyHot.ArcRouter.Quality.Parsing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Quality.Extraction;

/// <summary>
/// Extracts the first runnable fenced code block from an assistant response, preferring blocks whose
/// language is recognized, and tags it with an inferred dimension. Applies size and count caps.
/// </summary>
public sealed class CodeBlockSignalExtractor : ISignalExtractor
{
    private readonly IDimensionInferrer _dimensionInferrer;
    private readonly QualityOptions _options;
    private readonly ILogger<CodeBlockSignalExtractor> _logger;

    /// <summary>Initializes a new instance of the <see cref="CodeBlockSignalExtractor"/> class.</summary>
    /// <param name="dimensionInferrer">Infers the task dimension from the prompt and language.</param>
    /// <param name="options">The quality options (size/count caps).</param>
    /// <param name="logger">The logger.</param>
    public CodeBlockSignalExtractor(
        IDimensionInferrer dimensionInferrer,
        IOptions<QualityOptions> options,
        ILogger<CodeBlockSignalExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(dimensionInferrer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _dimensionInferrer = dimensionInferrer;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public QualityRequest? Extract(SignalExtractionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.ResponseText))
        {
            return null;
        }

        var blocks = FencedCodeBlockParser.Parse(context.ResponseText, _options.MaxCodeBlocks);
        if (blocks.Count == 0)
        {
            return null;
        }

        // Prefer the first block whose language we recognize; otherwise fall back to the first block.
        FencedCodeBlock chosen = blocks[0];
        var language = CodeLanguage.Unknown;
        foreach (var block in blocks)
        {
            var lang = CodeLanguages.FromHint(block.LanguageHint);
            if (lang != CodeLanguage.Unknown)
            {
                chosen = block;
                language = lang;
                break;
            }
        }

        var code = chosen.Code;
        if (code.Length > _options.MaxCodeBytes)
        {
            code = code[.._options.MaxCodeBytes];
        }

        var dimension = _dimensionInferrer.Infer(context.Prompt, language);

        _logger.LogDebug(
            "Extracted {Language} code block ({Length} chars) for dimension {Dimension} (correlation {CorrelationId}).",
            language,
            code.Length,
            dimension,
            context.CorrelationId);

        return new QualityRequest(
            Code: code,
            Language: language,
            Dimension: dimension,
            Model: context.Model,
            CorrelationId: context.CorrelationId,
            SessionId: context.SessionId);
    }
}

