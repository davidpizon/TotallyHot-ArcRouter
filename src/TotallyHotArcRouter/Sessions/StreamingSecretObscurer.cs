using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using TotallyHot.ArcRouter.Logging;

namespace TotallyHot.ArcRouter.Sessions;

/// <summary>
/// A write-only stream that obscures key-shaped strings (<see cref="SecretObscurer"/>'s patterns) in a body
/// as it passes through, holding only a bounded look-back window instead of the whole body (ADR-0019,
/// "Bounded capture"). The output equals <c>SecretObscurer.Obscure</c> over the whole body, run per maximal
/// valid UTF-8 run with invalid bytes copied through untouched, with two deliberate exceptions that both err
/// toward redacting more:
/// <list type="bullet">
/// <item>A run of base64, base64url or dot characters longer than <see cref="RunCollapseChars"/> is redacted as one token whatever
/// follows it, including a run that the one-shot pattern would leave alone because three or more <c>=</c>
/// follow it.</item>
/// <item>The text in front of such a run is emitted without the match context that would have joined the two
/// (<c>Bearer </c> followed by a long run keeps its <c>Bearer </c>).</item>
/// </list>
/// A match that grows past <see cref="DefaultWindowChars"/> before it can be decided cannot be obscured
/// without either leaking part of a secret or holding the body, so the capture is abandoned with
/// <see cref="SessionCaptureAbandonedException"/>. Call <see cref="Finish"/> to flush the held tail; a stream
/// that was only disposed has not written a complete body.
/// </summary>
public sealed class StreamingSecretObscurer : Stream
{
    /// <summary>How many characters of undecided text the obscurer holds before it abandons the capture.</summary>
    public const int DefaultWindowChars = 64 * 1024;

    /// <summary>A trailing base64, base64url or dot run longer than this is redacted without being held.</summary>
    public const int RunCollapseChars = 4096;

    /// <summary>The most input processed per step, which bounds the character buffer beside the window.</summary>
    private const int SliceBytes = 16 * 1024;

    private const int PemHeaderLookBackChars = 160;
    private const string PemBegin = "-----BEGIN";

    private static readonly Regex KeyShaped = SecretObscurer.Pattern();
    private static readonly byte[] RedactedBytes = Encoding.UTF8.GetBytes(SecretObscurer.RedactedToken);

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly int _windowChars;
    private readonly byte[] _carry = new byte[3];
    private char[] _chars = new char[SliceBytes];
    private int _count;
    private int _carryLength;
    private bool _inRun;
    private bool _finished;
    private bool _abandoned;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingSecretObscurer"/> class.
    /// </summary>
    /// <param name="output">Receives the obscured bytes.</param>
    /// <param name="leaveOpen">Whether disposing this stream leaves <paramref name="output"/> open.</param>
    /// <param name="windowChars">The look-back window; a test passes a small one to reach the abandon path.</param>
    public StreamingSecretObscurer(Stream output, bool leaveOpen = false, int windowChars = DefaultWindowChars)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowChars, 1);
        _output = output;
        _leaveOpen = leaveOpen;
        _windowChars = windowChars;
    }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Flush() => _output.Flush();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    /// <summary>
    /// Obscures and forwards what can be decided, holding back only text that a later byte could still turn
    /// into a match.
    /// </summary>
    /// <param name="buffer">The next bytes of the body.</param>
    /// <exception cref="SessionCaptureAbandonedException">When a match outgrows the window; the stream is unusable afterwards.</exception>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfAbandonedOrFinished();

        try
        {
            while (!buffer.IsEmpty)
            {
                var slice = Math.Min(buffer.Length, SliceBytes);
                Feed(buffer[..slice]);
                buffer = buffer[slice..];
            }
        }
        catch (SessionCaptureAbandonedException)
        {
            Abandon();
            throw;
        }
    }

    /// <summary>
    /// Flushes the held tail and any trailing bytes of an incomplete UTF-8 sequence (which are invalid, so
    /// they pass through). Idempotent. Does not close the output.
    /// </summary>
    /// <exception cref="SessionCaptureAbandonedException">When the stream was already abandoned.</exception>
    public void Finish()
    {
        if (_finished) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_abandoned) throw new SessionCaptureAbandonedException("The secret obscurer was abandoned.");

        ProcessPending(final: true);
        var carry = _carry.AsSpan(0, _carryLength);
        _carryLength = 0;
        while (!carry.IsEmpty)
        {
            Rune.DecodeFromUtf8(carry, out _, out var consumed);
            var invalid = Math.Max(consumed, 1);
            _output.Write(carry[..invalid]);
            carry = carry[invalid..];
        }

        _finished = true;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Array.Clear(_chars);
            Array.Clear(_carry);
            if (!_leaveOpen) _output.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Whether a character can belong to, or sit against, a key-shaped match: a regex word character (which
    /// decides <c>\b</c>) or one of the punctuation marks the patterns use. Text is only ever held from the
    /// start of a run of these, so word-boundary context survives the cut.
    /// </summary>
    /// <param name="c">The character to test.</param>
    /// <returns><see langword="true"/> when the character can be part of a token.</returns>
    private static bool IsTokenChar(char c) => IsWordChar(c) || c is '-' or '+' or '/' or '=' or '.';

    /// <summary>The .NET <c>\w</c> class: letters, non-spacing marks, decimal digits, connector punctuation.</summary>
    private static bool IsWordChar(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.ConnectorPunctuation;

    /// <summary>
    /// Whether a character can sit in a long unbroken run that is redacted without being held: the base64 and
    /// base64url alphabets plus <c>.</c>, so an encoded blob or a JWT-shaped string over
    /// <see cref="RunCollapseChars"/> is redacted instead of outgrowing the window.
    /// </summary>
    /// <param name="c">The character to test.</param>
    /// <returns><see langword="true"/> when the character can belong to such a run.</returns>
    private static bool IsRunChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '-' or '_' or '.';

    /// <summary>
    /// Finds where the longest safely decidable prefix of <paramref name="text"/> ends. Everything before the
    /// result is final: no later byte can change what a match inside it covers. The result is the earliest of
    /// the start of the trailing token run, the start of a <c>Bearer</c> whose token has not arrived, the start
    /// of a PEM header still being typed, the start of any match that reaches the end of the text, and the
    /// start of any match that straddles the cut.
    /// </summary>
    /// <param name="text">The undecided text, from the end of what was already emitted.</param>
    /// <returns>The number of leading characters that may be emitted.</returns>
    private static int ComputeBoundary(ReadOnlySpan<char> text)
    {
        var length = text.Length;
        var boundary = length;
        while (boundary > 0 && IsTokenChar(text[boundary - 1])) boundary--;

        var afterBearer = boundary;
        while (afterBearer > 0 && char.IsWhiteSpace(text[afterBearer - 1])) afterBearer--;
        const int bearerLength = 6;
        if (afterBearer < boundary && afterBearer >= bearerLength
            && text[(afterBearer - bearerLength)..afterBearer].Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            && (afterBearer == bearerLength || !IsWordChar(text[afterBearer - bearerLength - 1])))
        {
            boundary = afterBearer - bearerLength;
        }

        var pemSearchStart = Math.Max(0, length - PemHeaderLookBackChars);
        var pem = text[pemSearchStart..].LastIndexOf(PemBegin, StringComparison.OrdinalIgnoreCase);
        if (pem >= 0 && IsUnfinishedPemHeader(text[(pemSearchStart + pem + PemBegin.Length)..]))
        {
            boundary = Math.Min(boundary, pemSearchStart + pem);
        }

        foreach (var match in KeyShaped.EnumerateMatches(text))
        {
            var end = match.Index + match.Length;
            if (end == length || (match.Index < boundary && boundary < end))
            {
                boundary = Math.Min(boundary, match.Index);
            }
        }

        return boundary;
    }

    /// <summary>
    /// Whether the text after <c>-----BEGIN</c> could still grow into a private-key header: only letters and
    /// spaces, then at most four of the five closing dashes.
    /// </summary>
    /// <param name="rest">The characters after <c>-----BEGIN</c>.</param>
    /// <returns><see langword="true"/> when the header is unfinished.</returns>
    private static bool IsUnfinishedPemHeader(ReadOnlySpan<char> rest)
    {
        var i = 0;
        while (i < rest.Length && (rest[i] == ' ' || rest[i] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) i++;
        var dashes = rest.Length - i;
        if (dashes > 4) return false;
        for (; i < rest.Length; i++)
        {
            if (rest[i] != '-') return false;
        }

        return true;
    }

    /// <summary>
    /// Decodes one slice of input (with any bytes carried from the previous write) into the pending text, and
    /// emits what is decidable. An invalid byte flushes the valid run and is copied through; an incomplete
    /// sequence at the end of the slice is carried to the next write.
    /// </summary>
    /// <param name="input">At most <c>SliceBytes</c> of the body.</param>
    private void Feed(ReadOnlySpan<byte> input)
    {
        byte[]? rented = null;
        var data = input;
        if (_carryLength > 0)
        {
            rented = ArrayPool<byte>.Shared.Rent(_carryLength + input.Length);
            _carry.AsSpan(0, _carryLength).CopyTo(rented);
            input.CopyTo(rented.AsSpan(_carryLength));
            data = rented.AsSpan(0, _carryLength + input.Length);
            _carryLength = 0;
        }

        try
        {
            while (!data.IsEmpty)
            {
                EnsureCapacity(_count + data.Length);
                var status = Utf8.ToUtf16(
                    data, _chars.AsSpan(_count), out var read, out var written,
                    replaceInvalidSequences: false, isFinalBlock: false);
                _count += written;
                data = data[read..];

                if (status == OperationStatus.InvalidData)
                {
                    // An invalid byte ends the valid run exactly as it does in the one-shot obscurer.
                    ProcessPending(final: true);
                    Rune.DecodeFromUtf8(data, out _, out var consumed);
                    var invalid = Math.Max(consumed, 1);
                    _output.Write(data[..invalid]);
                    data = data[invalid..];
                    continue;
                }

                if (status == OperationStatus.NeedMoreData)
                {
                    data.CopyTo(_carry);
                    _carryLength = data.Length;
                    data = default;
                }

                ProcessPending(final: false);
            }
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>
    /// Emits what is decidable from the pending text and keeps the rest. With <paramref name="final"/>, the
    /// text ends (end of body or an invalid byte), so everything is decided.
    /// </summary>
    /// <param name="final">Whether no further text can follow the pending text in this valid run.</param>
    private void ProcessPending(bool final)
    {
        var text = _chars.AsSpan(0, _count);
        if (_inRun)
        {
            var skip = 0;
            while (skip < text.Length && (IsRunChar(text[skip]) || text[skip] == '=')) skip++;
            if (skip == text.Length)
            {
                _count = 0;
                if (final) _inRun = false;
                return;
            }

            _inRun = false;
            Discard(skip);
            text = _chars.AsSpan(0, _count);
        }

        if (text.IsEmpty) return;

        if (final)
        {
            EmitReplaced(text, text.Length);
            _count = 0;
            return;
        }

        var runStart = text.Length;
        while (runStart > 0 && IsRunChar(text[runStart - 1])) runStart--;
        if (text.Length - runStart > RunCollapseChars)
        {
            EmitReplaced(text, runStart);
            _output.Write(RedactedBytes);
            _count = 0;
            _inRun = true;
            return;
        }

        var boundary = ComputeBoundary(text);
        if (text.Length - boundary > _windowChars)
        {
            throw new SessionCaptureAbandonedException(
                "A possible secret outgrew the obscurer's look-back window, so the body was not captured.");
        }

        if (boundary == 0) return;
        EmitReplaced(text, boundary);
        Discard(boundary);
    }

    /// <summary>
    /// Writes <c>text[..limit]</c> with each match that ends within the limit replaced by the redaction
    /// token. Matches are found over the whole text so lookahead and word-boundary context are real.
    /// </summary>
    /// <param name="text">The pending text.</param>
    /// <param name="limit">How many leading characters to emit.</param>
    private void EmitReplaced(ReadOnlySpan<char> text, int limit)
    {
        var cursor = 0;
        foreach (var match in KeyShaped.EnumerateMatches(text))
        {
            if (match.Index + match.Length > limit) break;
            WriteText(text[cursor..match.Index]);
            _output.Write(RedactedBytes);
            cursor = match.Index + match.Length;
        }

        WriteText(text[cursor..limit]);
    }

    /// <summary>Encodes text as UTF-8 and writes it to the output.</summary>
    /// <param name="text">Text that is already decided, possibly empty.</param>
    private void WriteText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        var rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(text.Length));
        try
        {
            var written = Encoding.UTF8.GetBytes(text, rented);
            _output.Write(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>Drops the first <paramref name="chars"/> characters of the pending text, which were emitted or skipped.</summary>
    /// <param name="chars">How many leading characters to drop.</param>
    private void Discard(int chars)
    {
        _chars.AsSpan(chars, _count - chars).CopyTo(_chars);
        _count -= chars;
    }

    /// <summary>Grows the pending-text buffer, clearing the old one, so it can hold <paramref name="needed"/> characters.</summary>
    /// <param name="needed">The total characters the buffer must hold.</param>
    private void EnsureCapacity(int needed)
    {
        if (needed <= _chars.Length) return;
        var bigger = new char[Math.Max(needed, _chars.Length * 2)];
        _chars.AsSpan(0, _count).CopyTo(bigger);
        Array.Clear(_chars);
        _chars = bigger;
    }

    /// <summary>Marks the stream unusable and clears every buffer that held body text.</summary>
    private void Abandon()
    {
        _abandoned = true;
        _count = 0;
        _carryLength = 0;
        Array.Clear(_chars);
        Array.Clear(_carry);
    }

    /// <summary>Rejects a write after the stream was abandoned or finished.</summary>
    /// <exception cref="SessionCaptureAbandonedException">When the stream was abandoned.</exception>
    /// <exception cref="InvalidOperationException">When the stream was already finished.</exception>
    private void ThrowIfAbandonedOrFinished()
    {
        if (_abandoned) throw new SessionCaptureAbandonedException("The secret obscurer was abandoned.");
        if (_finished) throw new InvalidOperationException("The secret obscurer was already finished.");
    }
}
