using System.Text;
using TotallyHot.ArcRouter.Logging;
using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins <see cref="StreamingSecretObscurer"/> to the one-shot <see cref="SecretObscurer"/>: whatever the chunk
/// boundaries, the streamed output must equal the one-shot output, except where the streaming version
/// deliberately redacts more (a very long base64 run) or gives up (a match outgrowing its window).
/// </summary>
public sealed class StreamingSecretObscurerTests
{
    private const string AnthropicKey = "sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
    private const string PemKey =
        "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEAxxxxxxxxxxxxxxxxxxxx\nyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy\n-----END RSA PRIVATE KEY-----";

    /// <summary>Texts that exercise each pattern, boundaries, and look-around context.</summary>
    public static TheoryData<string> Corpus() =>
    [
        "",
        "plain text with nothing secret in it",
        $"key={AnthropicKey} and more",
        $"{AnthropicKey}",
        $"x{AnthropicKey}",
        $"prefix {AnthropicKey}suffix words",
        "token xoxb-1234567890-abcdefghij end",
        "gh: ghp_abcdefghijklmnopqrstuvwxyz0123 done",
        "aws AKIAIOSFODNN7EXAMPLE and ASIAABCDEFGHIJKLMNOP.",
        "google AIzaSyA-abcdefghijklmnopqrstuvwxyz012345 ok",
        "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789 trailing",
        "Authorization:   bearer\tabcdefghijklmnopqrstuvwxyz0123456789",
        "short Bearer abc then more",
        $"before\n{PemKey}\nafter",
        "-----BEGIN RSA PRIVATE KEY-----\nMIIEow (never terminated) tail words here",
        "-----BEGIN CERTIFICATE-----\nnot a private key\n-----END CERTIFICATE-----",
        "base64 QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5YWJjZGVm== end",
        "slash/path/ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnop/12345 end",
        "unicode héllo 🙂 wörld sk-abcdefghijklmnopqrstuvwxyz and 日本語",
        "Xsk-abcdefghijklmnopqrstuvwxyz is not at a word boundary",
        "two keys " + AnthropicKey + " and " + AnthropicKey + ".",
        "{\"authorization\":\"Bearer " + AnthropicKey + "\",\"model\":\"auto\"}",
    ];

    /// <summary>Every two-way and three-way split of every corpus text matches the one-shot result.</summary>
    [Theory]
    [MemberData(nameof(Corpus))]
    public void Stream_SplitAtEveryPosition_EqualsOneShot(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var expected = Encoding.UTF8.GetBytes(SecretObscurer.Obscure(text));

        for (var first = 0; first <= bytes.Length; first++)
        {
            Assert.Equal(expected, Run(bytes, [first]));
        }

        for (var first = 0; first <= bytes.Length; first += 3)
        {
            for (var second = first; second <= bytes.Length; second += 5)
            {
                Assert.Equal(expected, Run(bytes, [first, second]));
            }
        }

        Assert.Equal(expected, Run(bytes, Enumerable.Range(1, bytes.Length).ToArray()));
    }

    /// <summary>A seeded random mix of fragments streams the same as one-shot at random chunk sizes.</summary>
    [Fact]
    public void Stream_RandomFragmentsAndChunking_EqualsOneShot()
    {
        string[] fragments =
        [
            AnthropicKey, PemKey, "Bearer ", "Bearer abcdefghijklmnopqrstuvwxyz0123456789", " ", "\n", "-----BEGIN ",
            "PRIVATE KEY-----", "word", "sk-", "AKIAIOSFODNN7EXAMPLE", "é", "🙂", "==", "+/", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIz",
            "ghp_abcdefghijklmnopqrstuvwxyz0123", "{\"a\":\"b\"}", "-", "_", ".",
        ];
        var random = new Random(165);
        for (var round = 0; round < 400; round++)
        {
            var text = string.Concat(Enumerable.Range(0, random.Next(1, 14)).Select(_ => fragments[random.Next(fragments.Length)]));
            var bytes = Encoding.UTF8.GetBytes(text);
            var expected = Encoding.UTF8.GetBytes(SecretObscurer.Obscure(text));
            var cuts = Enumerable.Range(0, random.Next(0, 6)).Select(_ => random.Next(bytes.Length + 1)).OrderBy(c => c).ToArray();

            Assert.True(expected.AsSpan().SequenceEqual(Run(bytes, cuts)), $"round {round}: {text}");
        }
    }

    /// <summary>Invalid bytes pass through untouched and a multi-byte character split across writes survives.</summary>
    [Fact]
    public void Stream_InvalidUtf8AndSplitCharacters_PassThroughUnchanged()
    {
        var body = new List<byte>();
        body.AddRange("ok é 🙂 "u8.ToArray());
        body.AddRange([0xFF, 0xC3]);
        body.AddRange(Encoding.UTF8.GetBytes($" {AnthropicKey} "));
        body.AddRange([0xE2, 0x82]);
        body.AddRange(" end"u8.ToArray());
        body.Add(0xE2);

        var whole = Run(body.ToArray(), []);
        for (var cut = 0; cut <= body.Count; cut++)
        {
            Assert.Equal(whole, Run(body.ToArray(), [cut]));
        }

        Assert.DoesNotContain(AnthropicKey, Encoding.Latin1.GetString(whole));
        Assert.Contains("[REDACTED]", Encoding.Latin1.GetString(whole));
        Assert.Equal(0xFF, whole[whole.ToList().IndexOf(0xFF)]);
        Assert.Equal(0xE2, whole[^1]);
        Assert.True(whole.AsSpan().IndexOf("é 🙂"u8) >= 0);
    }

    /// <summary>A base64 run far past the collapse threshold is redacted whole, in bounded memory.</summary>
    [Fact]
    public void Stream_HugeBase64Run_IsRedactedAsOneTokenAcrossChunks()
    {
        var run = new string('A', 3_000_000);
        var text = $"before {run}== after";
        var output = new MemoryStream();
        using (var obscurer = new StreamingSecretObscurer(output, leaveOpen: true))
        {
            foreach (var chunk in Encoding.UTF8.GetBytes(text).Chunk(7_001)) obscurer.Write(chunk);
            obscurer.Finish();
        }

        Assert.Equal("before [REDACTED] after", Encoding.UTF8.GetString(output.ToArray()));
    }

    /// <summary>An unbroken base64url or dotted blob over the window is redacted whole instead of abandoning the capture.</summary>
    [Fact]
    public void Stream_HugeBase64UrlBlob_IsRedactedNotAbandoned()
    {
        var blob = string.Concat(Enumerable.Repeat("eyJhbGciOi-_.JIUzI1NiJ9", 8_000));
        var output = new MemoryStream();
        using (var obscurer = new StreamingSecretObscurer(output, leaveOpen: true))
        {
            foreach (var chunk in Encoding.UTF8.GetBytes($"data:{blob} tail").Chunk(10_000)) obscurer.Write(chunk);
            obscurer.Finish();
        }

        Assert.Equal("data:[REDACTED] tail", Encoding.UTF8.GetString(output.ToArray()));
    }

    /// <summary>A match that cannot be decided inside the window abandons the capture rather than leaking part of it.</summary>
    [Fact]
    public void Stream_UnterminatedKeyBeyondWindow_Abandons()
    {
        var output = new MemoryStream();
        using var obscurer = new StreamingSecretObscurer(output, leaveOpen: true, windowChars: 512);
        obscurer.Write("-----BEGIN RSA PRIVATE KEY-----\n"u8);

        var ex = Record.Exception(() =>
        {
            for (var i = 0; i < 100; i++) obscurer.Write(Encoding.UTF8.GetBytes(new string('M', 63) + "\n"));
        });

        Assert.IsType<SessionCaptureAbandonedException>(ex);
        Assert.DoesNotContain("MMMM", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Throws<SessionCaptureAbandonedException>(() => obscurer.Write("more"u8));
        Assert.Throws<SessionCaptureAbandonedException>(obscurer.Finish);
    }

    /// <summary>A terminated key larger than its window's worth of other text is still obscured.</summary>
    [Fact]
    public void Stream_LargeBodyOfOrdinaryText_HoldsOnlyAShortTail()
    {
        var text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 20_000)) + AnthropicKey;
        var bytes = Encoding.UTF8.GetBytes(text);

        var result = Run(bytes, [100_000, 400_000]);

        Assert.Equal(Encoding.UTF8.GetBytes(SecretObscurer.Obscure(text)), result);
    }

    private static byte[] Run(byte[] body, int[] cuts)
    {
        var output = new MemoryStream();
        using (var obscurer = new StreamingSecretObscurer(output, leaveOpen: true))
        {
            var start = 0;
            foreach (var cut in cuts.Append(body.Length))
            {
                var end = Math.Max(start, Math.Min(cut, body.Length));
                obscurer.Write(body.AsSpan(start, end - start));
                start = end;
            }

            obscurer.Finish();
        }

        return output.ToArray();
    }
}
