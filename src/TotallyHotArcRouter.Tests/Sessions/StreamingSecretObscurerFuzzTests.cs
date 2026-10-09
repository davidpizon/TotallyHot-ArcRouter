using System.Text;
using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Randomized pressure on <see cref="StreamingSecretObscurer"/>: whatever the mix of secrets, partial secrets,
/// zero-width characters, invalid bytes and chunk boundaries, the streamed output must equal the one-shot
/// obscurer's, and a very long run may be redacted more but never less. The seeds are fixed so a failure
/// reproduces.
/// </summary>
public sealed class StreamingSecretObscurerFuzzTests
{
    private static readonly string[] Secrets =
    [
        "sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789",
        "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEAxxxxxxxxxxxxxxxxxxxx\nyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy\n-----END RSA PRIVATE KEY-----",
        "Bearer abcdefghijklmnopqrstuvwxyz0123456789",
        "Bearer\tabcdefghijklmnopqrstuvwxyz0123456789",
        "bearer\n\nabcdefghijklmnopqrstuvwxyz0123456789",
        "AKIAIOSFODNN7EXAMPLE",
        "ghp_abcdefghijklmnopqrstuvwxyz0123",
        "xoxb-1234567890-abcdefghij",
        "AIzaSyA-abcdefghijklmnopqrstuvwxyz012345",
        "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5YWJjZGVm",
        "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5YWJjZGVm==",
        "github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz",
    ];

    private static readonly string[] Glue =
    [
        " ", "\n", "\t", "\"", ",", ":", "{", "}", "‍", "‌", "́", "é", "🙂", "日本語", "K", "ſ", "x",
        "xx", "-", "_", ".", "/", "+", "=", "==", "===", "Bearer", "Bearer ", "sk-", "sk", "-----BEGIN ",
        "PRIVATE KEY-----", "-----BEGIN RSA PRIV", "-----END RSA PRIVATE KEY-----", "AKIA", "ghp_", "xox", "AIza",
        "word", "A",
    ];

    private static readonly byte[] InvalidBytes = [0xFF, 0xC3, 0xE2, 0x82];

    /// <summary>Random mixes of secrets, fragments, zero-width characters and invalid bytes stream exactly like the one-shot obscurer.</summary>
    [Theory]
    [InlineData(20261008)]
    [InlineData(165)]
    [InlineData(4096)]
    public void Stream_RandomMixesAtRandomCuts_EqualOneShot(int seed)
    {
        var random = new Random(seed);
        for (var round = 0; round < 3_000; round++)
        {
            var body = new List<byte>();
            for (var part = random.Next(1, 12); part > 0; part--)
            {
                var pick = random.Next(10);
                var piece = pick < 4
                    ? Secrets[random.Next(Secrets.Length)]
                    : pick < 9 ? Glue[random.Next(Glue.Length)] : new string('A', new[] { 1, 39, 40, 41, 100 }[random.Next(5)]);
                body.AddRange(Encoding.UTF8.GetBytes(piece));
                if (random.Next(12) == 0) body.Add(InvalidBytes[random.Next(InvalidBytes.Length)]);
            }

            var bytes = body.ToArray();
            var cuts = Enumerable.Range(0, random.Next(0, 6)).Select(_ => random.Next(bytes.Length + 1)).Order().ToArray();

            var expected = SessionRecordCodec.Decompress(SessionRecordCodec.ObscureAndCompress(bytes));
            var actual = Run(bytes, cuts);

            Assert.True(
                expected.AsSpan().SequenceEqual(actual),
                $"seed {seed} round {round} cuts [{string.Join(',', cuts)}]: {Escape(bytes)}\n  expected {Escape(expected)}\n  actual   {Escape(actual)}");
        }
    }

    /// <summary>Runs on both sides of the collapse threshold, with secrets before and after, never leave more than the one-shot obscurer leaves.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Stream_RunsAroundTheCollapseThreshold_NeverLeakMoreThanOneShot(int seed)
    {
        var random = new Random(seed);
        string[] alphabets =
        [
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/",
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_",
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.-_",
        ];
        for (var round = 0; round < 60; round++)
        {
            var alphabet = alphabets[random.Next(alphabets.Length)];
            var length = new[] { 4094, 4095, 4096, 4097, 4098, 6000, 20_000, 70_000 }[random.Next(8)];
            var run = new string(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var secret = Secrets[random.Next(Secrets.Length)];
            var text = $"before {secret} {run}{(random.Next(2) == 0 ? "==" : string.Empty)} middle {secret} after";
            var bytes = Encoding.UTF8.GetBytes(text);
            var cuts = Enumerable.Range(0, random.Next(0, 5)).Select(_ => random.Next(bytes.Length + 1)).Order().ToArray();

            var expected = SessionRecordCodec.Decompress(SessionRecordCodec.ObscureAndCompress(bytes));
            var actual = Run(bytes, cuts);

            foreach (var key in Secrets)
            {
                var needle = Encoding.UTF8.GetBytes(key);
                Assert.False(
                    actual.AsSpan().IndexOf(needle) >= 0 && expected.AsSpan().IndexOf(needle) < 0,
                    $"seed {seed} round {round}: the stream left a secret the one-shot obscurer removed.");
            }

            Assert.True(actual.Length <= expected.Length + 64, $"seed {seed} round {round}: the stream kept far more text than the one-shot obscurer.");
        }
    }

    private static string Escape(byte[] bytes) =>
        string.Concat(bytes.Select(b => b is >= 0x20 and < 0x7f ? ((char)b).ToString() : $"\\x{b:x2}"));

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
