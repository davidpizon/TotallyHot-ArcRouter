namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// One question put to an emulated model, and what its reply must yield after normalization.
/// </summary>
/// <param name="Name">Stable key this scenario's recorded reply is stored under in a <see cref="RecordedModelTranscript"/>.</param>
/// <param name="Tools">The tool schemas offered, as the client would send them.</param>
/// <param name="Question">The user turn.</param>
/// <param name="ExpectedCalls">
/// The function names the reply must normalize to, in order. <b>Empty means no call at all</b> - the
/// false-positive scenario, where tools are offered and none is needed.
/// </param>
internal sealed record EmulationScenario(
    string Name,
    IReadOnlyList<string> Tools,
    string Question,
    IReadOnlyList<string> ExpectedCalls);

/// <summary>
/// The fixed probe used to judge whether a model can be emulated at all
/// (<c>docs/router/tool-call-normalization.md</c> Phase 5).
///
/// <para>
/// Held in one place because two things consume it: the replay tests, which run every recorded model
/// against it offline, and the recording recipe in <see cref="RecordedModelTranscripts"/>, which needs the
/// identical questions or a new model's transcript would not be comparable to an existing one.
/// </para>
///
/// <para>
/// Five scenarios chosen to separate the ways emulation fails rather than to cover surface area. A model
/// can frame one call correctly and still lose the arguments; can frame a call and pick the wrong tool
/// from three; can do all of that and then invent a call when asked a question needing none, which is the
/// false positive per-model detection exists to prevent; and can do all of *that* and emit only one call
/// when two were asked for.
/// </para>
/// </summary>
internal static class ToolCallEmulationScenarios
{
    internal const string GetTime =
        """{"type":"function","function":{"name":"get_time","description":"Returns the current time in a given timezone.","parameters":{"type":"object","properties":{"timezone":{"type":"string","description":"IANA timezone name"}},"required":["timezone"]}}}""";

    internal const string GetWeather =
        """{"type":"function","function":{"name":"get_weather","description":"Returns the current weather for a city.","parameters":{"type":"object","properties":{"city":{"type":"string"},"units":{"type":"string","enum":["metric","imperial"]}},"required":["city"]}}}""";

    internal const string ReadFile =
        """{"type":"function","function":{"name":"read_file","description":"Reads a file from disk and returns its contents.","parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}}""";

    public static IReadOnlyList<EmulationScenario> All { get; } =
    [
        new("single", [GetTime], "What time is it in Tokyo?", ["get_time"]),
        new("arguments", [GetWeather], "What's the weather in Paris in celsius?", ["get_weather"]),
        new("pick-one-of-three", [GetTime, GetWeather, ReadFile], "Read the contents of /etc/hosts for me.", ["read_file"]),
        new("no-tool-needed", [GetTime, GetWeather], "Write a haiku about the sea. Do not use any tools.", []),
        new("two-at-once", [GetTime, GetWeather], "Get me both the time in Tokyo and the weather in Paris.", ["get_time", "get_weather"]),
    ];
}

