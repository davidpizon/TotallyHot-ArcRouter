using System.Globalization;
using System.Text;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// Pulls the <c>content</c> string out of a <c>{"content", "tool_calls"}</c> envelope
/// <em>
/// while it is still
/// arriving
/// </em>
/// , decoding JSON escapes as it goes, so a constrained reply's prose can be streamed to the
/// client token by token instead of appearing all at once when the envelope closes.
/// <para>
/// This type is the reason constrained decoding does not cost the user a visibly worse chat experience.
/// Under <see cref="ToolCallDialectRegistry.Constrained"/> the model emits JSON, not prose, so forwarding
/// <c>delta.content</c> untouched would stream raw <c>{"content": "…</c> into the chat window; buffering
/// until the envelope is complete would fix that but make every reply arrive in one lump, which - since
/// agent-mode clients attach tools to nearly every request - is most replies. Decoding the string
/// incrementally gives the client ordinary prose deltas with no idea a grammar was involved.
/// </para>
/// <para>
/// A real JSON scanner rather than a search for <c>"content":</c>, because that substring can legitimately
/// occur inside a tool argument. Only a string opened at depth 1 whose key was <c>content</c> is streamed,
/// which a nesting-aware walk can tell apart and a substring search cannot. Stateful, one instance per
/// response, not thread-safe.
/// </para>
/// </summary>
internal sealed class EnvelopeContentScanner
{
    // Container nesting: true for an object, false for an array. Depth is the stack count, so a string
    // opened while exactly one object is open is a top-level property of the envelope.
    private readonly Stack<bool> _containers = new();

    private readonly StringBuilder _currentString = new();

    // Text received but not yet consumable - a lone trailing backslash, or an incomplete \uXXXX, whose
    // decoding needs bytes that have not arrived.
    private readonly StringBuilder _pending = new();
    private bool _expectKey;

    // A high surrogate whose partner has not arrived. JSON writes a non-BMP character - any emoji - as two
    // consecutive \uXXXX escapes, and each decodes to one UTF-16 char. Emitting the first on its own would
    // hand the caller a string ending in a lone surrogate, which is not valid UTF-16: encoding it produces
    // U+FFFD, so the client renders a replacement character instead of the emoji. Observed live before this
    // was held back - a haiku came through as "moonlit sky<?>". The pair must cross the boundary together.
    private char? _heldHighSurrogate;

    private bool _inString;
    private bool _isKeyString;
    private string? _lastKey;

    /// <summary>Whether the scanner is currently inside the envelope's <c>content</c> string.</summary>
    private bool _streamingContent;

    /// <summary>Gets a value indicating whether the <c>content</c> string has been fully read.</summary>
    /// <remarks>
    /// Lets the caller distinguish "the model wrote no prose" from "the prose never finished arriving",
    /// which matters on the fail-open path: only the second means text may still be owed to the client.
    /// </remarks>
    public bool ContentComplete { get; private set; }

    /// <summary>Gets a value indicating whether any prose has been emitted yet.</summary>
    /// <remarks>
    /// The fail-open decision hinges on this. If the envelope turns out to be unparseable and nothing was
    /// emitted, the caller can safely forward the whole raw text; if prose was already streamed, forwarding
    /// the raw text too would repeat it and expose the JSON scaffolding around it.
    /// </remarks>
    public bool HasEmitted { get; private set; }

    /// <summary>
    /// Feeds newly-arrived envelope text in and returns whatever decoded prose is now safe to emit.
    /// </summary>
    /// <param name="text">The next slice of the model's raw <c>content</c>.</param>
    /// <returns>Decoded prose, or an empty string when this slice revealed none.</returns>
    public string Push(string text)
    {
        _pending.Append(text);

        var emitted = new StringBuilder();

        // Re-attach a surrogate held back last time, so the pair is emitted as one character.
        if (_heldHighSurrogate is { } held)
        {
            emitted.Append(held);
            _heldHighSurrogate = null;
        }

        var buffer = _pending.ToString();
        var consumed = 0;

        while (consumed < buffer.Length)
        {
            var c = buffer[consumed];

            if (_inString)
            {
                if (c == '\\')
                {
                    // An escape needs at least one more character, and \u needs five. Stop rather than
                    // guess: the remainder stays pending until the rest of the sequence arrives.
                    if (!TryReadEscape(buffer: buffer, index: consumed, decoded: out var decoded,
                            length: out var escapeLength)) break;

                    if (_streamingContent)
                        emitted.Append(decoded);
                    else
                        _currentString.Append(decoded);

                    consumed += escapeLength;
                    continue;
                }

                if (c == '"')
                {
                    EndString();
                    consumed++;
                    continue;
                }

                if (_streamingContent)
                    emitted.Append(c);
                else
                    _currentString.Append(c);

                consumed++;
                continue;
            }

            switch (c)
            {
                case '"':
                    BeginString();
                    break;
                case '{':
                    _containers.Push(true);
                    _expectKey = true;
                    break;
                case '[':
                    _containers.Push(false);
                    _expectKey = false;
                    break;
                case '}' or ']':
                    if (_containers.Count > 0) _containers.Pop();

                    _expectKey = false;
                    break;
                case ',':
                    // A comma returns an object to key position but leaves an array in value position.
                    _expectKey = _containers.Count > 0 && _containers.Peek();
                    break;
                case ':':
                    _expectKey = false;
                    break;
            }

            consumed++;
        }

        _pending.Remove(0, length: consumed);

        // Never end an emission on a lone high surrogate - hold it for the next call, which is where its
        // low half will arrive. Only the trailing char can be orphaned: any earlier one already has its
        // partner in this same buffer.
        if (emitted.Length > 0 && char.IsHighSurrogate(emitted[^1]))
        {
            _heldHighSurrogate = emitted[^1];
            emitted.Length--;
        }

        var result = emitted.ToString();
        if (result.Length > 0) HasEmitted = true;

        return result;
    }

    /// <summary>Opens a string, deciding up front whether it is a key, and whether it is the one to stream.</summary>
    private void BeginString()
    {
        _inString = true;
        _isKeyString = _expectKey;
        _currentString.Clear();

        // Depth 1 means a direct property of the envelope object. A `content` key nested inside a tool
        // argument sits at depth 3 or deeper and must not hijack the stream.
        _streamingContent = !_isKeyString
                            && !ContentComplete
                            && _containers.Count == 1
                            && string.Equals(a: _lastKey, b: ToolCallEnvelopeSchema.ContentProperty,
                                comparisonType: StringComparison.Ordinal);
    }

    /// <summary>Closes a string, recording it as the current key or ending the streamed content.</summary>
    private void EndString()
    {
        _inString = false;

        if (_isKeyString)
        {
            _lastKey = _currentString.ToString();
        }
        else if (_streamingContent)
        {
            ContentComplete = true;
            _streamingContent = false;

            // A surrogate still held when the string closes never got its partner, so the source was
            // malformed. Discard it: a lone surrogate cannot be encoded, and carrying it forward would
            // prepend a replacement character to whatever is emitted next.
            _heldHighSurrogate = null;
        }

        _currentString.Clear();
    }

    /// <summary>
    /// Decodes one backslash escape starting at <paramref name="index"/>, or reports that the sequence is
    /// not yet complete.
    /// </summary>
    /// <remarks>
    /// A <c>\uXXXX</c> escape decodes to a single <see cref="char"/>, so a surrogate pair - which JSON
    /// writes as two consecutive escapes - is reconstructed correctly by appending each half in turn.
    /// An escape this does not recognize yields the escaped character itself, which is what a lenient JSON
    /// reader does and keeps a malformed stream readable rather than truncating it.
    /// </remarks>
    private static bool TryReadEscape(string buffer, int index, out string decoded, out int length)
    {
        decoded = string.Empty;
        length = 0;

        if (index + 1 >= buffer.Length) return false;

        var escape = buffer[index + 1];
        if (escape == 'u')
        {
            if (index + 5 >= buffer.Length) return false;

            var hex = buffer.Substring(startIndex: index + 2, 4);
            if (!ushort.TryParse(s: hex, style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture,
                    result: out var code))
            {
                // Not a valid escape at all - pass it through verbatim rather than dropping it.
                decoded = buffer.Substring(startIndex: index, 6);
                length = 6;
                return true;
            }

            decoded = ((char)code).ToString();
            length = 6;
            return true;
        }

        decoded = escape switch
        {
            'n' => "\n",
            't' => "\t",
            'r' => "\r",
            'b' => "\b",
            'f' => "\f",
            _ => escape.ToString()
        };

        length = 2;
        return true;
    }
}