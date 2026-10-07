using Microsoft.Extensions.Options;
using Serilog.Context;
using TotallyHot.ArcRouter.Proxy;

namespace TotallyHot.ArcRouter.Logging;

/// <summary>
/// Shared names and emit helpers for the four conversation-bearing interceptor templates (#184 F9).
/// Marked events carry <see cref="PropertyName"/> so Serilog can route them only to <c>bodies-*.log</c>
/// and so ADR-0020 can later recognize them on <c>StreamEvents</c>.
/// </summary>
public static class ConversationBodyLogging
{
    /// <summary>
    /// Serilog property name stamped on every body-excerpt event. Diagnostic sinks exclude events that
    /// carry it; the body sink includes only those events.
    /// </summary>
    public const string PropertyName = "ConversationBody";

    /// <summary>Fixed message template for the full request-body excerpt.</summary>
    public const string InterceptedRequestMessage = "[INTERCEPTOR] Intercepted agent request message: {RequestBody}";

    /// <summary>Fixed message template for the newest user message excerpt.</summary>
    public const string NewestUserMessage = "[INTERCEPTOR] Newest user message: {UserMessage}";

    /// <summary>Fixed message template for the full response-body excerpt.</summary>
    public const string InterceptedResponseMessage =
        "[INTERCEPTOR] Intercepted agent response message: {ResponseBody}";

    /// <summary>Fixed message template for the assembled response-text excerpt.</summary>
    public const string AssembledResponseText = "[INTERCEPTOR] Assembled LLM response text: {ResponseText}";

    /// <summary>
    /// The four F9 message prefixes used by the pre-upgrade log rewrite to recognize legacy Debug lines
    /// that still hold conversation text. Kept as substrings so both Serilog's rendered form and older
    /// console copies match.
    /// </summary>
    public static readonly string[] LegacyMessagePrefixes =
    [
        "[INTERCEPTOR] Intercepted agent request message:",
        "[INTERCEPTOR] Newest user message:",
        "[INTERCEPTOR] Intercepted agent response message:",
        "[INTERCEPTOR] Assembled LLM response text:"
    ];

    /// <summary>
    /// Returns whether body-excerpt logging is currently on. A missing monitor is treated as off so
    /// unit tests that construct the proxy without the options type stay silent.
    /// </summary>
    private static bool IsEnabled(IOptionsMonitor<BodyExcerptOptions>? options)
    {
        return options?.CurrentValue.Enabled == true;
    }

    /// <summary>
    /// Emits one marked Information line when body excerpts are enabled. The argument is truncated,
    /// CR/LF-sanitized, and secret-obscured before it is placed in the template.
    /// </summary>
    /// <param name="logger">The MEL logger that forwards into Serilog.</param>
    /// <param name="options">Live body-excerpt switch; <see langword="null"/> means off.</param>
    /// <param name="messageTemplate">One of the four F9 templates (static string literal).</param>
    /// <param name="bodyText">The (possibly large) text to log.</param>
    public static void LogExcerpt(
        ILogger logger,
        IOptionsMonitor<BodyExcerptOptions>? options,
        string messageTemplate,
        string? bodyText)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTemplate);

        if (!IsEnabled(options)) return;

        var prepared = SecretObscurer.Obscure(LogRedaction.TruncateSanitize(bodyText));
        using (LogContext.PushProperty(PropertyName, true))
            logger.LogInformation(messageTemplate, prepared);
    }

    /// <summary>
    /// Same as the string overload for a UTF-8 payload that has not yet been decoded (response
    /// capture path).
    /// </summary>
    public static void LogExcerpt(
        ILogger logger,
        IOptionsMonitor<BodyExcerptOptions>? options,
        string messageTemplate,
        ReadOnlySpan<byte> utf8Body)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTemplate);

        if (!IsEnabled(options)) return;

        var prepared = SecretObscurer.Obscure(LogRedaction.DecodeTruncateSanitize(utf8Body));
        using (LogContext.PushProperty(PropertyName, true))
            logger.LogInformation(messageTemplate, prepared);
    }
}
