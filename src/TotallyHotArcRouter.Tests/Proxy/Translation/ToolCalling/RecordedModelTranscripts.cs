namespace TotallyHot.ArcRouter.Tests.Proxy.Translation.ToolCalling;

/// <summary>
/// One model's recorded replies to every <see cref="ToolCallEmulationScenarios"/> question.
/// </summary>
/// <param name="ModelName">The model that produced these, for failure messages.</param>
/// <param name="Notes">How this model behaved - the summary a reader needs before interpreting a failure.</param>
/// <param name="ResponseByScenario">
/// The complete upstream response body for each <see cref="EmulationScenario.Name"/>, exactly as the
/// server returned it. The whole envelope is kept rather than just the assistant text, because the
/// envelope is where the server's own quirks live - LM Studio's unconditional <c>"tool_calls": []</c>
/// being the one that permanently mis-classified every model it served until it was found.
/// </param>
/// <param name="EmulationFailures">
/// The scenarios this model does <b>not</b> emulate correctly, mapped to the calls the shipped pipeline
/// actually yields for them - almost always none, because an unrecognized framing yields nothing rather
/// than yielding something wrong.
/// <para>
/// A model that fails is kept rather than dropped, so the recorded reply is what a reader consults when
/// asking whether emulation is worth attempting for a given family. Recording the <em>observed</em>
/// result rather than a bare "this one fails" flag is what makes the entry self-correcting: the replay
/// theory asserts both that the pipeline still produces exactly this, and that this still differs from
/// <see cref="EmulationScenario.ExpectedCalls"/>. A prompt change that fixes the model therefore fails
/// the test and forces the entry to be deleted, instead of leaving a stale failure recorded forever.
/// </para>
/// </param>
internal sealed record RecordedModelTranscript(
    string ModelName,
    string Notes,
    IReadOnlyDictionary<string, string> ResponseByScenario,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? EmulationFailures = null);

/// <summary>
/// Recorded model replies, so emulation can be judged against a real model without one running.
/// <para>
/// <b>Adding a model.</b> Load it in your OpenAI-compatible server and record its replies to
/// <see cref="ToolCallEmulationScenarios.All"/> - the same questions, or the result is not comparable to
/// an existing transcript. For each scenario: build a normal chat request carrying that scenario's
/// <c>tools</c>, pass the body through
/// <see cref="TotallyHot.ArcRouter.Proxy.Translation.ToolCalling.ToolCallEmulationRewriter"/>
/// (do <em>not</em> hand-write the emulated prompt - the point is to measure what the shipped rewriter
/// produces), POST it, and store the response body verbatim under the scenario name. Then add the
/// transcript to <see cref="All"/> and the replay theory covers it automatically.
/// </para>
/// <para>
/// A new transcript that fails is a <b>finding, not a broken test</b>: it means emulation does not work
/// for that model, which is worth knowing and worth keeping. Record it, list the failing scenarios in
/// <see cref="RecordedModelTranscript.EmulationFailures"/> against what the pipeline actually yields for
/// them, summarize the behavior in <see cref="RecordedModelTranscript.Notes"/>, and assert the failure
/// rather than deleting it - the rejected-variant captures in <see cref="ToolCallEmulationCaptureTests"/>
/// exist for the same reason. <see cref="DeepSeekR1DistillQwen7B"/> is the worked example.
/// </para>
/// </summary>
internal static class RecordedModelTranscripts
{
    /// <summary>
    /// The model from the original incident, recorded 2026-07-31 from LM Studio at temperature 0 against
    /// the shipped emulation prompt.
    /// </summary>
    /// <remarks>
    /// Note what the assistant text actually is: every call is framed in <c>&lt;tools&gt;</c>, not the
    /// <c>&lt;tool_call&gt;</c> the prompt asks for. That is the blend the emulated dialect registers a
    /// second delimiter for, and these are the replies that proved it happens on every request rather than
    /// occasionally. Note also <c>"tool_calls": []</c> on all five, including the prose answer.
    /// <para>
    /// Field values are verbatim; the JSON is reformatted to one line per response.
    /// </para>
    /// </remarks>
    public static RecordedModelTranscript Qwen25Coder7BInstruct { get; } = new(
        ModelName: "qwen2.5.1-coder-7b-instruct",
        Notes: "Passes all five scenarios. Frames every call in <tools> rather than the requested <tool_call>.",
        ResponseByScenario: new Dictionary<string, string>
        {
            ["single"] =
                """
                {"id":"chatcmpl-7njt60psmkjkeabst905o","object":"chat.completion","created":1785472762,"model":"qwen2.5.1-coder-7b-instruct","choices":[{"index":0,"message":{"role":"assistant","content":"<tools>{\"name\":\"get_time\",\"arguments\":{\"timezone\":\"Asia/Tokyo\"}}</tools>","reasoning_content":"","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":148,"completion_tokens":21,"total_tokens":169,"completion_tokens_details":{"reasoning_tokens":0}},"stats":{},"system_fingerprint":"qwen2.5.1-coder-7b-instruct"}
                """,

            ["arguments"] =
                """
                {"id":"chatcmpl-ws4phvj9p0q3bol8hunbj6","object":"chat.completion","created":1785472767,"model":"qwen2.5.1-coder-7b-instruct","choices":[{"index":0,"message":{"role":"assistant","content":"<tools>{\"name\":\"get_weather\",\"arguments\":{\"city\":\"Paris\",\"units\":\"metric\"}}</tools>","reasoning_content":"","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":157,"completion_tokens":22,"total_tokens":179,"completion_tokens_details":{"reasoning_tokens":0}},"stats":{},"system_fingerprint":"qwen2.5.1-coder-7b-instruct"}
                """,

            ["pick-one-of-three"] =
                """
                {"id":"chatcmpl-2d0iwr90jdhawexxtnavhr","object":"chat.completion","created":1785472772,"model":"qwen2.5.1-coder-7b-instruct","choices":[{"index":0,"message":{"role":"assistant","content":"<tools>{\"name\":\"read_file\",\"arguments\":{\"path\":\"/etc/hosts\"}}</tools>","reasoning_content":"","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":255,"completion_tokens":20,"total_tokens":275,"completion_tokens_details":{"reasoning_tokens":0}},"stats":{},"system_fingerprint":"qwen2.5.1-coder-7b-instruct"}
                """,

            ["no-tool-needed"] =
                """
                {"id":"chatcmpl-s7nwqpycr6sgd77hi2nrr","object":"chat.completion","created":1785472777,"model":"qwen2.5.1-coder-7b-instruct","choices":[{"index":0,"message":{"role":"assistant","content":"Waves crash on shore,\nSeagulls cry in the breeze,\nOcean's vast embrace.","reasoning_content":"","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":212,"completion_tokens":20,"total_tokens":232,"completion_tokens_details":{"reasoning_tokens":0}},"stats":{},"system_fingerprint":"qwen2.5.1-coder-7b-instruct"}
                """,

            ["two-at-once"] =
                """
                {"id":"chatcmpl-bhe2xi5e7q8tu5wljmlchs","object":"chat.completion","created":1785472782,"model":"qwen2.5.1-coder-7b-instruct","choices":[{"index":0,"message":{"role":"assistant","content":"<tools>\n    {\"name\": \"get_time\", \"arguments\": {\"timezone\": \"Asia/Tokyo\"}}\n    {\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\", \"units\": \"metric\"}}\n</tools>","reasoning_content":"","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":211,"completion_tokens":50,"total_tokens":261,"completion_tokens_details":{"reasoning_tokens":0}},"stats":{},"system_fingerprint":"qwen2.5.1-coder-7b-instruct"}
                """
        });

    /// <summary>
    /// A reasoning model that <b>cannot</b> be emulated, recorded 2026-07-31 from LM Studio at temperature 0
    /// against the same shipped prompt. Kept because a negative result measured is worth more than a
    /// negative result assumed.
    /// </summary>
    /// <remarks>
    /// Four scenarios fail, and they fail four <em>different</em> ways - which is the finding. Asked for one
    /// call it invented <c>&lt;function-call&gt;</c>; asked to pick from three it emitted a fenced block
    /// keying the function as <c>"function"</c> rather than <c>"name"</c>; asked for two it emitted two
    /// fenced blocks and no delimiter at all. None of the four is a near-miss that a slightly wider scanner
    /// would rescue - they disagree with the prompt and with each other.
    /// <para>
    /// The fifth is the one to read closely. <c>no-tool-needed</c> is scored as a pass, and it is a pass in
    /// the only sense that matters - no call reaches the client - but the model did not answer in prose
    /// either. It emitted a tool call named after the first line of the haiku it was asked to write. It
    /// yields nothing solely because it framed that call in no delimiter, so the false-positive guard is
    /// being carried here by the same defect that breaks the other four. A future prompt that taught this
    /// model to frame its output would fix four scenarios and turn this one into a genuine false positive.
    /// </para>
    /// <para>
    /// Field values are verbatim, including the <c>reasoning_content</c> this model populates and the
    /// <c>"tool_calls": []</c> LM Studio adds on every buffered reply; the JSON is reformatted to one line
    /// per response.
    /// </para>
    /// </remarks>
    public static RecordedModelTranscript DeepSeekR1DistillQwen7B { get; } = new(
        ModelName: "deepseek-r1-distill-qwen-7b",
        Notes: "Fails four of five scenarios, each with a different invented framing (<function-call>, a "
               + "fenced block keyed \"function\", a fenced ```xml <function name=...> element, and bare fenced "
               + "JSON). Passes no-tool-needed only because its invented call is undelimited too.",
        ResponseByScenario: new Dictionary<string, string>
        {
            ["single"] =
                """
                {"id":"chatcmpl-a3eqcl8kuel2krj79szb9o","object":"chat.completion","created":1785505534,"model":"deepseek-r1-distill-qwen-7b","choices":[{"index":0,"message":{"role":"assistant","content":"\n\nTo determine the current time in Tokyo, you can use the `get_time` function with the appropriate timezone parameter. Here's how:\n\n<function-call>\n    {\"name\": \"get_time\", \"arguments\": {\"timezone\": \"Asia/Tokyo\"}}\n</function-call>\n\nThe JSON response will contain the current time in Tokyo based on your system's clock adjusted for that timezone.\n\n```json\n{\"name\": \"get_time\", \"arguments\": {\"timezone\": \"Asia/Tokyo\"}}\n```","reasoning_content":"\nOkay, the user asked, \"What time is it in Tokyo?\" I need to figure out how to respond using the provided tools. \n\nFirst, I look at the available function: get_time with a timezone parameter. That seems perfect for this query.\n\nI should call get_time and specify \"Asia/Tokyo\" as the timezone. This will return the current time in Tokyo.\n\nSo, my response should be a JSON object with the function name and the arguments including the timezone.\n","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":138,"completion_tokens":201,"total_tokens":339,"completion_tokens_details":{"reasoning_tokens":97}},"stats":{},"system_fingerprint":"deepseek-r1-distill-qwen-7b"}
                """,

            ["arguments"] =
                """
                {"id":"chatcmpl-8pksqfowkt32ehxnal9wpy","object":"chat.completion","created":1785505554,"model":"deepseek-r1-distill-qwen-7b","choices":[{"index":0,"message":{"role":"assistant","content":"\n\nTo determine the weather in Paris in Celsius, we can use the `get_weather` function with the city \"Paris\" and units set to \"metric\".\n\n<function-call>\n  {\"name\": \"get_weather\", \"arguments\": {\"city\": \"Paris\", \"units\": \"metric\"}}\n</function-call>\n\nThe expected JSON output would be:\n\n{\"current-temperature\": 22, \"condition\": \"sunny\", \"humidity\": 65, \"wind-speed\": 12}","reasoning_content":"\nOkay, so I need to figure out how to respond to the user's query about the weather in Paris in Celsius. The user has provided some tools, specifically a function called get_weather that takes a city and units as parameters. My task is to call this function with the appropriate arguments and return the result in a JSON format within XML tags.\n\nFirst, I should identify what information the user is asking for. They want the weather in Paris but specifically in Celsius. That means I need to use \"Paris\" as the city parameter and set the units to \"metric\" since Celsius is part of the metric system.\n\nI remember that the get_weather function returns a JSON object with details like current temperature, condition, humidity, and wind speed. But for this response, I only need the temperature in Celsius. So after calling the function, I'll extract just that piece of information.\n\nPutting it all together, I'll structure my answer by first stating the function name and arguments, then provide the expected JSON output. This way, the user gets both the function call details and the anticipated result clearly.\n","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":147,"completion_tokens":324,"total_tokens":471,"completion_tokens_details":{"reasoning_tokens":219}},"stats":{},"system_fingerprint":"deepseek-r1-distill-qwen-7b"}
                """,

            ["pick-one-of-three"] =
                """
                {"id":"chatcmpl-kgjdn2li9pdb4ey2iuui4l","object":"chat.completion","created":1785505642,"model":"deepseek-r1-distill-qwen-7b","choices":[{"index":0,"message":{"role":"assistant","content":"\n\nTo read the contents of /etc/hosts, you can use the `read_file` function with the appropriate path parameter.\n\n```json\n{\"function\":\"read_file\",\"arguments\":{\"path\":\"/etc/hosts\"}}\n```","reasoning_content":"\nOkay, so I need to figure out how to read the contents of /etc/hosts using one of the provided functions. Let's see what tools are available.\n\nI have three functions: get_time, get_weather, and read_file. The user asked to read a file from disk, which is exactly what read_file does. So that makes sense.\n\nLooking at the parameters for read_file, it requires a \"path\" which is a string. In this case, the path is \"/etc/hosts\". I should make sure to use the correct syntax for JSON within the arguments object.\n\nSo the function call would be something like {\"function\": \"read_file\", \"arguments\": {\"path\": \"/etc/hosts\"}}. That should do it.\n","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":245,"completion_tokens":199,"total_tokens":444,"completion_tokens_details":{"reasoning_tokens":150}},"stats":{},"system_fingerprint":"deepseek-r1-distill-qwen-7b"}
                """,

            ["no-tool-needed"] =
                """
                {"id":"chatcmpl-sr6wskxryml623m6tn31e","object":"chat.completion","created":1785505792,"model":"deepseek-r1-distill-qwen-7b","choices":[{"index":0,"message":{"role":"assistant","content":"\n\n{\"name\": \"Rustling waves crash.\", \"arguments\": {\"type\": \"string\", \"value\": \"Rustling waves crash.\"}}","reasoning_content":"\nAlright, so I need to write a haiku about the sea without using any tools. Hmm, okay, let's break this down.\n\nFirst, what is a haiku? It's a traditional Japanese poem with three lines. The syllable structure is typically 5-7-5. So each line should have that number of syllables.\n\nNow, focusing on the sea. I want to capture something about the sea in a concise and beautiful way. Maybe imagery related to the ocean, waves, or the vastness of it.\n\nLet me think of some words associated with the sea: waves, foam, moon, stars, night, sand, reflections... Hmm.\n\nFor the first line, maybe something like \"Morning's promise\" – that gives a sense of hope and anticipation. Then I need to fit it into 5 syllables. \"Morning's promise\" is two words, each with three syllables? Wait, no: \"morning\" (1), \"'s\" (no syllable), \"promise\" (2). So total of 3 syllables. Hmm, that's not enough for the first line.\n\nWait, maybe I can adjust it to have five syllables. Let me think again. Maybe \"Morning waves crash,\" but that might be too long. Or perhaps \"Moonlight dips,\" which is two syllables. Not quite.\n\nAlternatively, using a metaphor: \"Echoes of the sea\" – that's three syllables. Hmm, not enough.\n\nWait, maybe I can use more vivid imagery. How about \"Ripples dance\"? That's four syllables. Close but still not five.\n\nPerhaps combining elements like \"Sea's embrace,\" which is two syllables. Not enough either.\n\nHmm, perhaps the first line needs to be a bit longer or more descriptive. Maybe something like \"Morning's promise\" can be split into more words with syllable count in mind. Or maybe I'm overcomplicating it and should think of other lines first.\n\nLet me try focusing on the second line. It should have seven syllables. Maybe something about the sea at night: \"Stars above, waves flow.\" That's four syllables. Not enough. Maybe adding more words: \"Silent nights, waves crash.\" Still not there yet.\n\nWait, perhaps using a metaphor like \"Mirror of the sky\" – that's three syllables. Then maybe \"Reflecting depths,\" which is five syllables? Wait, no, \"reflecting\" is one syllable, \"depths\" another. So two syllables total. Hmm.\n\nAlternatively, \"Moonlight dips\" as before, but I need to make it seven syllables. Maybe \"Stars above, waves flow.\" That's four syllables in the first part and three in the second? Not quite.\n\nWait, perhaps combining elements: \"Silent nights, waves crash,\" that's six syllables. Close. Maybe adding another word for the seventh syllable: \"Echoes of the sea.\" So total would be \"Silent nights, waves crash, echoes of the sea.\" That's nine syllables across three lines, which is too much.\n\nWait, maybe I'm approaching this wrong. Let me try to structure each line with exactly five, seven, and five syllables respectively.\n\nFirst line: 5 syllables.\nSecond line: 7 syllables.\nThird line: 5 syllables.\n\nSo for the first line, perhaps \"Morning's promise\" is three syllables, so I need two more. Maybe adding a word like \"bright\" to make it \"Bright morning's promise.\" That's four syllables. Still not enough.\n\nAlternatively, \"Ripples dance,\" which is four syllables. Hmm.\n\nWait, maybe using alliteration: \"Sea's waves crash,\" but that's three syllables. Not enough.\n\nPerhaps I'm overcomplicating this and should think of a different approach. Maybe focusing on the sound of the sea rather than imagery. For example, \"Crash of waves\" – two syllables. Then adding more words to reach five: \"Echoes of the sea.\" That's four syllables. Hmm.\n\nWait, maybe combining it with something else: \"Rustling waves crash,\" which is three syllables. Still not enough.\n\nAlternatively, using a metaphor like \"Time's whisper\" – two syllables. Then adding more words to reach five: \"Whispers of the sea.\" That's four syllables again.\n\nHmm, perhaps I'm stuck here and should try moving on to the second line first.\n\nSecond line needs seven syllables. Maybe something like \"Stars above, waves flow,\" which is six syllables. Close. Adding another word for the seventh syllable: \"Echoes of the sea.\" So total would be \"Stars above, waves flow, echoes of the sea.\" That's nine syllables across three lines again.\n\nWait, maybe I'm not structuring it correctly. Let me try to think of each line separately with their syllable counts.\n\nFirst line (5 syllables): Maybe \"Morning's promise\" is too short. Alternatively, \"Bright waves crash,\" which is four syllables. Hmm.\n\nAlternatively, using a metaphor: \"Sea's embrace,\" three syllables. Not enough.\n\nWait, perhaps I can use more descriptive words. For example, \"Ripples dance,\" which is four syllables. Maybe adding another word for the fifth syllable: \"Dancing ripples.\" That's five syllables.\n\nSo first line: \"Dancing ripples.\"\n\nSecond line (7 syllables): Maybe something like \"Stars above, waves flow,\" but that's six syllables. Adding another word: \"Echoes of the sea\" makes it nine again. Hmm.\n\nAlternatively, breaking it down differently: \"Silent nights, waves crash,\" which is seven syllables? Let me count: \"Silent\" (1), \"nights,\" (2), \", \" (no syllable), \"waves\" (3), \"crash.\" (4). Wait, that's four syllables. Hmm.\n\nWait, maybe \"Stars above, waves flow,\" which is six syllables. Then adding another word for the seventh: \"Echoes of the sea.\" So total would be seven syllables across three lines? No, each line has its own count.\n\nWait, perhaps I'm overcomplicating it and should try to find a way to fit in seven syllables without splitting into multiple words. Maybe using alliteration or a more concise phrase: \"Stars above, waves flow,\" which is six syllables. Hmm.\n\nAlternatively, \"Silent nights, waves crash,\" which is five syllables? Wait, no: \"Silent\" (1), \"nights,\" (2), \", \" (no), \"waves\" (3), \"crash.\" (4). So four syllables. Not enough.\n\nHmm, maybe I'm approaching this wrong and should try to think of the third line first.\n\nThird line needs five syllables: Maybe \"Echoes of the sea\" – three syllables. Adding more words: \"Dancing ripples,\" which is five syllables. Wait, no: \"Echoes\" (1), \"of\" (2), \"the\" (3), \"sea.\" (4). So four syllables.\n\nAlternatively, \"Ripples dance,\" which is four syllables. Hmm.\n\nWait, perhaps combining it with something else: \"Rustling waves crash,\" which is three syllables. Then adding more words for the fifth syllable: \"Echoes of the sea.\" That's seven syllables across two lines again.\n\nI think I'm stuck here and might need to try a different approach or accept that my initial ideas aren't perfect but still work towards a haiku structure.\n","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":202,"completion_tokens":1619,"total_tokens":1821,"completion_tokens_details":{"reasoning_tokens":1584}},"stats":{},"system_fingerprint":"deepseek-r1-distill-qwen-7b"}
                """,

            ["two-at-once"] =
                """
                {"id":"chatcmpl-5ux5ezq3f36l9v3u5qla","object":"chat.completion","created":1785506070,"model":"deepseek-r1-distill-qwen-7b","choices":[{"index":0,"message":{"role":"assistant","content":"\n\nTo obtain the current time in Tokyo and the weather in Paris, we will use the `get_time` and `get_weather` functions respectively. \n\n1. **Get Current Time in Tokyo**: We call `get_time` with the timezone parameter set to \"Asia/Tokyo\".\n2. **Get Weather in Paris**: We call `get_weather` with the city parameter set to \"Paris\" and units set to \"metric\".\n\nHere is the JSON response:\n\n```json\n{\n  \"name\": \"get_time\",\n  \"arguments\": {\n    \"timezone\": \"Asia/Tokyo\"\n  }\n}\n```\n\n```json\n{\n  \"name\": \"get_weather\",\n  \"arguments\": {\n    \"city\": \"Paris\",\n    \"units\": \"metric\"\n  }\n}\n```","reasoning_content":"\nOkay, so I need to figure out how to get both the current time in Tokyo and the weather in Paris using the provided functions. Let's break this down step by step.\n\nFirst, looking at the tools available, there are two functions: get_time and get_weather. Each has specific parameters they require.\n\nFor get_time, the parameter is 'timezone', which expects an IANA timezone name. Tokyo is in Japan Standard Time (JST), which is UTC+9. So I'll need to pass \"Asia/Tokyo\" as the timezone.\n\nNext, for get_weather, it requires a 'city' and 'units'. The city is Paris, obviously, but units can be either metric or imperial. Since weather data typically uses metric units (like Celsius and mm), I'll set units to \"metric\".\n\nNow, putting this together, I need to call both functions in sequence. First, get the time using Tokyo's timezone, then retrieve the weather for Paris with metric units.\n\nI should structure the response as a JSON object containing both function calls with their respective arguments within <function> tags.\n","tool_calls":[]},"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":201,"completion_tokens":388,"total_tokens":589,"completion_tokens_details":{"reasoning_tokens":223}},"stats":{},"system_fingerprint":"deepseek-r1-distill-qwen-7b"}
                """
        },
        EmulationFailures: new Dictionary<string, IReadOnlyList<string>>
        {
            // Every failure yields *nothing*, which is the safe direction to fail in: an unrecognized
            // framing is left as prose for the user to read, not turned into a call nobody requested.
            ["single"] = [],
            ["arguments"] = [],
            ["pick-one-of-three"] = [],
            ["two-at-once"] = []
        });

    /// <summary>Every recorded model. Adding an entry extends the replay theory with no other change.</summary>
    public static IReadOnlyList<RecordedModelTranscript> All { get; } =
        [Qwen25Coder7BInstruct, DeepSeekR1DistillQwen7B];
}