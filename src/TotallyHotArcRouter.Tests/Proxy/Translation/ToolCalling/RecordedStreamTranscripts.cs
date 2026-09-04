namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// One model's recorded <em>streamed</em> reply to one scenario, reduced to the part a translator sees.
/// </summary>
/// <param name="ModelName">The model that produced these, for failure messages.</param>
/// <param name="ScenarioName">The <see cref="EmulationScenario.Name"/> this answers.</param>
/// <param name="Emulated">
/// <see langword="true"/> when the request was rewritten by
/// <see cref="TotallyHot.ArcRouter.Proxy.Translation.ToolCalling.ToolCallEmulationRewriter"/>;
/// <see langword="false"/> when its <c>tools</c> array was sent natively.
/// </param>
/// <param name="ContentDeltas">
/// Every <c>choices[0].delta.content</c> value the server sent, in order and unmerged.
/// <para>
/// The split points are the payload, not an artifact of how this was recorded. A delimiter arriving as
/// <c>"```"</c>, <c>"json"</c>, <c>"\n"</c>, <c>"{\""</c> across four chunks is the situation the stream
/// translator's pending-text state machine exists for, and a recording that merged them would exercise
/// none of it while still looking like a stream.
/// </para>
/// </param>
/// <param name="ToolCallDeltaCount">
/// How many deltas carried a <c>tool_calls</c> key at all - present or empty, since the distinction is
/// what the buffered path got wrong. Recorded as a count rather than asserted inline so the number itself
/// is data: it is zero everywhere today, and a server that starts sending the field would change it here
/// rather than silently altering what these transcripts prove.
/// </param>
internal sealed record RecordedStreamTranscript(
    string ModelName,
    string ScenarioName,
    bool Emulated,
    IReadOnlyList<string> ContentDeltas,
    int ToolCallDeltaCount);

/// <summary>
/// Streamed replies recorded alongside the buffered ones, because the two paths are not the same evidence.
/// <para>
/// <b>Why these are stored as content deltas rather than raw SSE.</b> The raw capture is 1.9 MB, almost
/// all of it this model's <c>reasoning_content</c> deltas - 1,584 of them on one scenario, each carrying a
/// single token of the model failing to count haiku syllables.
/// <see cref="TotallyHot.ArcRouter.Proxy.Translation.ToolCalling.ToolCallNormalizingStreamTranslator"/> reads
/// exactly two fields, <c>delta.content</c> and <c>delta.tool_calls</c>, and clones every other field
/// through untouched, so the content sequence plus <see cref="RecordedStreamTranscript.ToolCallDeltaCount"/>
/// is the complete set of inputs that can change its behavior. This follows the precedent already set by
/// the token-by-token fragment sequence in <see cref="ToolCallNormalizingTranslatorTests"/> rather than
/// introducing a fixture-file pattern this project has never used.
/// </para>
/// <para>
/// <b>The finding worth reading first</b> is <see cref="RecordedStreamTranscript.ToolCallDeltaCount"/>
/// being zero on all ten. LM Studio puts <c>"tool_calls": []</c> on every buffered response - the quirk
/// that mis-classified an entire provider - and omits the field entirely when streaming. The stream
/// translator already assumed this in a comment; these are the ten recordings that measure it.
/// </para>
/// </summary>
internal static class RecordedStreamTranscripts
{
    private const string Model = "deepseek-r1-distill-qwen-7b";

    /// <summary>
    /// Every recorded stream: five scenarios each, emulated and native, recorded 2026-07-31 from LM Studio
    /// at temperature 0.
    /// </summary>
    public static IReadOnlyList<RecordedStreamTranscript> All { get; } =
    [
        new(ModelName: Model, ScenarioName: "single", true, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "To", " determine", " the", " current", " time",
            " in", " Tokyo", ",", " you", " can", " use",
            " the", " `", "get", "_time", "`", " function",
            " with", " the", " appropriate", " timezone", " parameter", ".\n\n",
            "```", "json", "\n", "{\"", "name", "\":\"",
            "get", "_time", "\",\"", "arguments", "\":{\"", "timezone",
            "\":\"", "Asia", "/T", "ok", "yo", "\"}}\n",
            "```"
        ]),

        new(ModelName: Model, ScenarioName: "arguments", true, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "{\"", "name", "\":", " \"", "get",
            "_weather", "\",", " \"", "arguments", "\":", " \"",
            "city", "='", "Paris", "',", " units", "='",
            "metric", "'", "\"}"
        ]),

        new(ModelName: Model, ScenarioName: "pick-one-of-three", true, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "To", " accomplish", " this", " task", ",",
            " we", " will", " use", " the", " `", "read",
            "_file", "`", " function", " provided", " in", " the",
            " tools", ".", " This", " function", " allows", " us",
            " to", " read", " the", " contents", " of", " a",
            " file", " from", " disk", " and", " return", " its",
            " contents", " as", " a", " JSON", " object", ".\n\n",
            "Here", "'s", " how", " we", " can", " achieve",
            " reading", " the", " contents", " of", " `/", "etc",
            "/", "hosts", "`", ":\n\n", "```", "xml",
            "\n", "<", "function", " name", "=\"", "read",
            "_file", "\"", " arguments", "={\"", "path", "\":",
            " \"/", "etc", "/", "hosts", "\"}", " />\n",
            "``", "`\n\n", "This", " will", " return", " a",
            " JSON", " object", " containing", " the", " contents", " of",
            " the", " `/", "etc", "/", "hosts", "`",
            " file", ".", " The", " `", "read", "_file",
            "`", " function", " is", " well", "-su", "ited",
            " for", " this", " task", " as", " it", " directly",
            " supports", " reading", " files", " from", " disk", ",",
            " which", " is", " exactly", " what", " we", " need",
            " to", " do", " in", " this", " case", ".\n\n",
            "If", " you", " encounter", " any", " issues", " with",
            " the", " file", " not", " existing", " or", " being",
            " empty", ",", " you", " might", " want", " to",
            " add", " error", " handling", " logic", ",", " but",
            " that", " would", " be", " beyond", " the", " current",
            " scope", " of", " just", " using", " the", " provided",
            " function", "."
        ]),

        new(ModelName: Model, ScenarioName: "no-tool-needed", true, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "{\"", "name", "\":", " \"", "R",
            "ust", "ling", " waves", " crash", ".\",", " \"",
            "arguments", "\":", " {\"", "type", "\":", " \"",
            "string", "\",", " \"", "value", "\":", " \"",
            "R", "ust", "ling", " waves", " crash", ".\"",
            "}}"
        ]),

        new(ModelName: Model, ScenarioName: "two-at-once", true, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "To", " obtain", " the", " current", " time",
            " in", " Tokyo", " and", " the", " weather", " in",
            " Paris", ",", " we", " will", " use", " the",
            " `", "get", "_time", "`", " and", " `",
            "get", "_weather", "`", " functions", " respectively", ".",
            " \n\n", "1", ".", " **", "Get", " Current",
            " Time", " in", " Tokyo", "**:", " We", " call",
            " `", "get", "_time", "`", " with", " the",
            " timezone", " parameter", " set", " to", " \"", "Asia",
            "/T", "ok", "yo", "\".\n", "2", ".",
            " **", "Get", " Weather", " in", " Paris", "**:",
            " We", " call", " `", "get", "_weather", "`",
            " with", " the", " city", " parameter", " set", " to",
            " \"", "Paris", "\"", " and", " units", " set",
            " to", " \"", "metric", "\".\n\n", "Here", " is",
            " the", " JSON", " response", ":\n\n", "```", "json",
            "\n", "{\n", " ", " \"", "name", "\":",
            " \"", "get", "_time", "\",\n", " ", " \"",
            "arguments", "\":", " {\n", "   ", " \"", "timezone",
            "\":", " \"", "Asia", "/T", "ok", "yo",
            "\"\n", " ", " }\n", "}\n", "``", "`\n\n",
            "```", "json", "\n", "{\n", " ", " \"",
            "name", "\":", " \"", "get", "_weather", "\",\n",
            " ", " \"", "arguments", "\":", " {\n", "   ",
            " \"", "city", "\":", " \"", "Paris", "\",\n",
            "   ", " \"", "units", "\":", " \"", "metric",
            "\"\n", " ", " }\n", "}\n", "```"
        ]),

        new(ModelName: Model, ScenarioName: "single", false, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "Hi", " there", "!", " I", " suggest",
            " getting", " online", " to", " get", " real", "-time",
            " information", ".", " If", " you", " have", " any",
            " other", " questions", ",", " please", " don", "'t",
            " hesitate", " to", " let", " me", " know", "!"
        ]),

        new(ModelName: Model, ScenarioName: "arguments", false, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "The", " weather", " in", " Paris", " can",
            " vary", " significantly", " depending", " on", " the", " time",
            " of", " year", " and", " specific", " conditions", " like",
            " clouds", " or", " wind", ".", " To", " get",
            " the", " current", " temperature", ":\n\n", "1", ".",
            " **", "Check", " Real", "-Time", " Data", "**:",
            " Use", " weather", " apps", " or", " online", " services",
            " such", " as", " Acc", "u", "Weather", ",",
            " which", " provide", " updated", " temperatures", " every", " few",
            " minutes", ".\n\n", "2", ".", " **", "Hour",
            "ly", " Fore", "casts", "**:", " These", " apps",
            " also", " offer", " hourly", " forecasts", ",", " so",
            " you", " can", " check", " at", " your", " specific",
            " time", " of", " interest", ".\n\n", "3", ".",
            " **", "Season", "al", " Consider", "ations", "**:",
            " Remember", " that", " temperatures", " in", " Paris", " typically",
            " range", " between", " ", "1", "0", "°C",
            " to", " ", "2", "5", "°C", " (",
            "5", "0", "°F", " to", " ", "7",
            "7", "°F", ")", " during", " summer", " and",
            " are", " cooler", " in", " winter", ",", " usually",
            " around", " ", "4", "°C", " to", " ",
            "1", "6", "°C", " (", "3", "9",
            "°F", " to", " ", "6", "1", "°F",
            ").\n\n", "For", " example", ",", " mid", "day",
            " temperatures", " often", " hover", " around", " ", "2",
            "2", "°C", " (", "7", "2", "°F",
            "),", " but", " this", " can", " fluct", "uate",
            " based", " on", " current", " conditions", ".", " Always",
            " check", " multiple", " sources", " for", " the", " most",
            " accurate", " information", "."
        ]),

        new(ModelName: Model, ScenarioName: "pick-one-of-three", false, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "To", " retrieve", " the", " content", " of",
            " the", " /", "etc", "/", "hosts", " file",
            " safely", ",", " follow", " these", " steps", ":\n\n",
            "1", ".", " **", "Check", " Permissions", ":",
            "**\n", "  ", " Use", " the", " command", " `",
            "ls", " -", "l", " /", "etc", "/",
            "hosts", "`", " to", " check", " if", " you",
            " have", " read", " permissions", " for", " the", " file",
            ".\n\n", "2", ".", " **", "Display", " File",
            " Contents", " (", "if", " permitted", "):", "**\n",
            "  ", " If", " you", " have", " access", ",",
            " use", " `", "cat", " /", "etc", "/",
            "hosts", "`", " to", " view", " the", " mappings",
            ".\n\n", "3", ".", " **", "Handle", " Access",
            " Den", "ial", ":", "**\n", "  ", " If",
            " denied", ",", " request", " permission", " and", " ensure",
            " only", " authorized", " users", " can", " access", " this",
            " file", ".\n\n", "The", " /", "etc", "/",
            "hosts", " file", " typically", " includes", " entries", " like",
            " localhost", " followed", " by", " other", " host", "names",
            " mapped", " to", " their", " IPv", "4", " addresses",
            ".", " Always", " handle", " such", " files", " with",
            " appropriate", " permissions", " to", " maintain", " security", "."
        ]),

        new(ModelName: Model, ScenarioName: "no-tool-needed", false, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "**", "Cr", "ab", " Cl", "aws",
            " Catch", " At", " Shore", ",**", "  \n", "**",
            "Sun", "light", " Sp", "ills", " Across", " Waves",
            ",**", "  \n", "**", "T", "ort", "o",
            "ises", " Pace", " Along", " Beach", ".", "**\n\n",
            "This", " ha", "iku", " captures", " the", " rhythm",
            " of", " the", " sea", " with", " imagery", " of",
            " movement", " and", " still", "ness", ",", " reflecting",
            " both", " activity", " and", " tranqu", "ility", "."
        ]),

        new(ModelName: Model, ScenarioName: "two-at-once", false, ToolCallDeltaCount: 0, ContentDeltas:
        [
            "\n\n", "To", " obtain", " both", " the", " current",
            " time", " in", " Tokyo", " and", " the", " weather",
            " in", " Paris", ",", " follow", " these", " steps",
            ":\n\n", "1", ".", " **", "Current", " Time",
            " in", " Tokyo", ":", "**\n", "  ", " -",
            " Use", " a", " reliable", " online", " time", " zone",
            " converter", " or", " website", " such", " as", " Time",
            "zone", " Converter", " (", "https", "://", "www",
            ".time", "zone", "converter", ".com", "/", ").",
            " Enter", " Paris", " as", " the", " destination", " city",
            " to", " get", " the", " current", " time", " in",
            " Tokyo", ".\n\n", "2", ".", " **", "Weather",
            " in", " Paris", ":", "**\n", "  ", " -",
            " Visit", " a", " trusted", " weather", " service", " website",
            " like", " Weather", " Underground", " (", "https", "://",
            "www", ".w", "under", "ground", ".com", ")",
            " or", " Acc", "u", "Weather", " (", "https",
            "://", "www", ".acc", "u", "weather", ".com",
            ").", " Enter", " Paris", " into", " the", " location",
            " field", " and", " check", " for", " real", "-time",
            " weather", " conditions", ",", " ensuring", " you", " select",
            " the", " correct", " time", " zone", " if", " needed",
            ".\n\n", "By", " using", " these", " methods", ",",
            " you", " can", " efficiently", " retrieve", " both", " the",
            " current", " time", " in", " Tokyo", " and", " the",
            " latest", " weather", " information", " for", " Paris", "."
        ])
    ];
}