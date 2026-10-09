using System.Text;
using TotallyHot.ArcRouter.Logging;
using TotallyHot.ArcRouter.Sessions;

namespace TotallyHot.ArcRouter.Tests.Sessions;

/// <summary>
/// Pins ADR-0019 session-file round-trips for #165 phase 1: sealed bodies survive write/read, and
/// planted secrets never land in the decrypted payload.
/// </summary>
public sealed class SessionFileTests
{
    /// <summary>A request/response pair round-trips byte-exact when it contains no secrets.</summary>
    [Fact]
    public void AppendAndRead_RoundTripsBodiesWithoutSecrets()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var archiveSessionId = SessionArchiveIds.NewArchiveSessionId();
            var archiveTurnId = SessionArchiveIds.NewArchiveTurnId();
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            var request = """{"messages":[{"role":"user","content":"hello"}]}"""u8.ToArray();
            var response = """{"content":[{"type":"text","text":"hi"}]}"""u8.ToArray();
            var providerRequest = """{"model":"translated"}"""u8.ToArray();
            var providerResponse = """{"candidates":[]}"""u8.ToArray();

            using (var file = SessionFile.Create(path, archiveSessionId, (byte[])sessionKey.Clone()))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, request, archiveTurnId);
                file.AppendBody(0, SessionBodyKind.ClientResponse, response, archiveTurnId);
                file.AppendBody(0, SessionBodyKind.ProviderRequest, providerRequest, archiveTurnId);
                file.AppendBody(0, SessionBodyKind.ProviderResponse, providerResponse, archiveTurnId);
            }

            using var reopened = SessionFile.Open(path, sessionKey, archiveSessionId);
            var frames = reopened.ReadAllBodies();

            Assert.Equal(4, frames.Count);
            Assert.Equal(request, frames[0].Plaintext);
            Assert.Equal(response, frames[1].Plaintext);
            Assert.Equal(providerRequest, frames[2].Plaintext);
            Assert.Equal(providerResponse, frames[3].Plaintext);
            Assert.Equal(archiveTurnId, frames[0].ArchiveTurnId);
            Assert.Equal(SessionBodyKind.ClientRequest, frames[0].Kind);
            Assert.Equal(SessionBodyKind.ClientResponse, frames[1].Kind);
            Assert.Equal(SessionBodyKind.ProviderRequest, frames[2].Kind);
            Assert.Equal(SessionBodyKind.ProviderResponse, frames[3].Kind);
            Assert.Equal(archiveSessionId, reopened.ArchiveSessionId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Key-shaped secrets are replaced before encryption, so they never leave the seal.</summary>
    [Fact]
    public void AppendAndRead_ObscuresPlantedApiKey()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            const string secret = "sk-abcdefghijklmnopqrstuvwxyz012345";
            var body = Encoding.UTF8.GetBytes("{\"token\":\"" + secret + "\"}");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();

            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), (byte[])sessionKey.Clone()))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, body, SessionArchiveIds.NewArchiveTurnId());
            }

            using var reopened = SessionFile.Open(path, sessionKey);
            var frames = reopened.ReadAllBodies();
            var text = Encoding.UTF8.GetString(frames[0].Plaintext!);

            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.Contains(SecretObscurer.RedactedToken, text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A missing body is recorded without inventing empty ciphertext.</summary>
    [Fact]
    public void AppendMissingBody_ReadsAsNullPlaintext()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            var turnId = SessionArchiveIds.NewArchiveTurnId();

            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), (byte[])sessionKey.Clone()))
            {
                file.AppendMissingBody(0, SessionBodyKind.ClientResponse, turnId);
            }

            using var reopened = SessionFile.Open(path, sessionKey);
            var frames = reopened.ReadAllBodies();

            Assert.Single(frames);
            Assert.Null(frames[0].Plaintext);
            Assert.Equal(turnId, frames[0].ArchiveTurnId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Bytes that are not valid UTF-8 survive the seal untouched, even beside a secret that is obscured.</summary>
    [Fact]
    public void AppendAndRead_PreservesInvalidUtf8Bytes()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            byte[] body = [0xFF, 0xFE, (byte)'a', 0xC3, 0x28, 0xE2, 0x82, (byte)'z', 0x80];

            byte[] withSecret = [.. body, .. "AKIAIOSFODNN7EXAMPLE"u8, 0xFF, .. body];
            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), (byte[])sessionKey.Clone()))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, body, SessionArchiveIds.NewArchiveTurnId());
                file.AppendBody(0, SessionBodyKind.ClientResponse, withSecret, SessionArchiveIds.NewArchiveTurnId());
            }

            using var reopened = SessionFile.Open(path, sessionKey);
            var frames = reopened.ReadAllBodies();

            Assert.Equal(body, frames[0].Plaintext);
            byte[] expected = [.. body, .. Encoding.UTF8.GetBytes(SecretObscurer.RedactedToken), 0xFF, .. body];
            Assert.Equal(expected, frames[1].Plaintext);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Altering a missing-body marker's kind on disk fails authentication.</summary>
    [Fact]
    public void ReadAllBodies_RejectsRelabelledMissingMarker()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), sessionKey))
            {
                file.AppendMissingBody(0, SessionBodyKind.ClientRequest, SessionArchiveIds.NewArchiveTurnId());
            }

            var bytes = File.ReadAllBytes(path);
            bytes[8 + 16 + 2] = (byte)SessionBodyKind.ClientResponse; // first frame's kind byte
            File.WriteAllBytes(path, bytes);

            using var reopened = SessionFile.Open(path, sessionKey);
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(reopened.ReadAllBodies);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Swapping two whole frames fails authentication because each is bound to its file position.</summary>
    [Fact]
    public void ReadAllBodies_RejectsReorderedFrames()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            const int dataStart = 8 + 16 + 2;
            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), sessionKey))
            {
                file.AppendMissingBody(0, SessionBodyKind.ClientRequest, SessionArchiveIds.NewArchiveTurnId());
                file.AppendMissingBody(1, SessionBodyKind.ClientRequest, SessionArchiveIds.NewArchiveTurnId());
            }

            var bytes = File.ReadAllBytes(path);
            var frameLength = (bytes.Length - dataStart) / 2;
            var swapped = new byte[bytes.Length];
            bytes.AsSpan(0, dataStart).CopyTo(swapped);
            bytes.AsSpan(dataStart + frameLength, frameLength).CopyTo(swapped.AsSpan(dataStart));
            bytes.AsSpan(dataStart, frameLength).CopyTo(swapped.AsSpan(dataStart + frameLength));
            File.WriteAllBytes(path, swapped);

            using var reopened = SessionFile.Open(path, sessionKey);
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(reopened.ReadAllBodies);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Frames appended after a reopen continue the file's position count and still authenticate.</summary>
    [Fact]
    public void Open_ThenAppend_ContinuesFramePositions()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), (byte[])sessionKey.Clone()))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, "one"u8, SessionArchiveIds.NewArchiveTurnId());
            }

            using (var file = SessionFile.Open(path, (byte[])sessionKey.Clone()))
            {
                file.AppendMissingBody(0, SessionBodyKind.ClientResponse, SessionArchiveIds.NewArchiveTurnId());
            }

            using var reopened = SessionFile.Open(path, sessionKey);
            var frames = reopened.ReadAllBodies();

            Assert.Equal(2, frames.Count);
            Assert.Equal("one"u8.ToArray(), frames[0].Plaintext);
            Assert.Null(frames[1].Plaintext);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Wrap then unwrap recovers the same session key under a master key.</summary>
    [Fact]
    public void WrapAndUnwrap_RoundTripsSessionKey()
    {
        var master = SessionKeyMaterial.CreateMasterKey();
        var session = SessionKeyMaterial.CreateSessionKey();

        Assert.Equal(SessionKeyMaterial.KeyLengthBytes, session.Length);
        Assert.Equal(SessionKeyMaterial.KeyLengthBytes, master.Length);

        var wrapped = SessionKeyMaterial.WrapSessionKey(master, session);
        var unwrapped = SessionKeyMaterial.UnwrapSessionKey(master, wrapped);

        Assert.Equal(session, unwrapped);
    }

    /// <summary>The file works on its own copy of the key, so disposing it never zeroes the caller's array.</summary>
    [Fact]
    public void Dispose_DoesNotZeroCallerKey()
    {
        var dir = CreateTempDir();
        try
        {
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            var expected = (byte[])sessionKey.Clone();

            SessionFile.Create(Path.Combine(dir, "session.bin"), SessionArchiveIds.NewArchiveSessionId(), sessionKey).Dispose();

            Assert.Equal(expected, sessionKey);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A crash mid-append leaves a partial frame; reopening drops it so later appends stay readable.</summary>
    [Fact]
    public void Open_TruncatesTornTrailingFrame()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            var turnId = SessionArchiveIds.NewArchiveTurnId();
            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), sessionKey))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, "one"u8, turnId);
                file.AppendBody(0, SessionBodyKind.ClientResponse, "two"u8, turnId);
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.SetLength(stream.Length - 5);
            }

            using (var reopened = SessionFile.Open(path, sessionKey))
            {
                reopened.AppendBody(1, SessionBodyKind.ClientRequest, "three"u8, turnId);
                var frames = reopened.ReadAllBodies();

                Assert.Equal(2, frames.Count);
                Assert.Equal("one"u8.ToArray(), frames[0].Plaintext);
                Assert.Equal("three"u8.ToArray(), frames[1].Plaintext);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Relabelling a frame's kind on disk fails authentication instead of silently swapping bodies.</summary>
    [Fact]
    public void ReadAllBodies_RejectsRelabelledFrame()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), sessionKey))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, "one"u8, SessionArchiveIds.NewArchiveTurnId());
            }

            var bytes = File.ReadAllBytes(path);
            bytes[8 + 16 + 2] = (byte)SessionBodyKind.ClientResponse; // first frame's kind byte
            File.WriteAllBytes(path, bytes);

            using var reopened = SessionFile.Open(path, sessionKey);
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(reopened.ReadAllBodies);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Opening with another session's id in the index is rejected.</summary>
    [Fact]
    public void Open_RejectsMismatchedArchiveId()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), sessionKey).Dispose();

            Assert.Throws<InvalidDataException>(() => SessionFile.Open(path, sessionKey, SessionArchiveIds.NewArchiveSessionId()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A body that compresses to several chunks reads back whole, and its neighbours are undisturbed.</summary>
    [Fact]
    public void AppendAndRead_MultiChunkBodyRoundTrips()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            var turnId = SessionArchiveIds.NewArchiveTurnId();
            var big = new byte[300_000];
            new Random(165).NextBytes(big);
            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), sessionKey))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, "before"u8, turnId);
                file.AppendBody(0, SessionBodyKind.ClientResponse, big, turnId);
                file.AppendBody(1, SessionBodyKind.ClientRequest, "after"u8, turnId);
            }

            using var reopened = SessionFile.Open(path, sessionKey);
            var frames = reopened.ReadAllBodies();

            Assert.Equal(3, frames.Count);
            Assert.Equal("before"u8.ToArray(), frames[0].Plaintext);
            Assert.True(big.AsSpan().SequenceEqual(frames[1].Plaintext));
            Assert.Equal("after"u8.ToArray(), frames[2].Plaintext);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A multi-chunk body cut off before its final chunk is a torn frame, not a shorter body.</summary>
    [Fact]
    public void Open_BodyCutBeforeItsFinalChunk_IsDroppedWhole()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            var turnId = SessionArchiveIds.NewArchiveTurnId();
            var big = new byte[300_000];
            new Random(2).NextBytes(big);
            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), sessionKey))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, "kept"u8, turnId);
                file.AppendBody(0, SessionBodyKind.ClientResponse, big, turnId);
            }

            // Cut at a chunk boundary: the first two chunks of the large body survive, the final one does not.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.SetLength(stream.Length - 40_000);
            }

            using var reopened = SessionFile.Open(path, sessionKey);

            var frames = reopened.ReadAllBodies();
            Assert.Single(frames);
            Assert.Equal("kept"u8.ToArray(), frames[0].Plaintext);
            Assert.Equal(1UL, reopened.FrameCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A body whose final chunk loses its final flag is a torn frame: it is dropped whole, never read as complete.</summary>
    [Fact]
    public void Open_BodyWithoutFinalFlag_IsDroppedAsTorn()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            using (var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), sessionKey))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, "one"u8, SessionArchiveIds.NewArchiveTurnId());
            }

            // 8 magic + 16 session id + 2 version + 22 frame header: the first chunk's final flag is next.
            const int finalFlagOffset = 8 + 16 + 2 + 22;
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(1, bytes[finalFlagOffset]);
            bytes[finalFlagOffset] = 0;
            File.WriteAllBytes(path, bytes);

            using var reopened = SessionFile.Open(path, sessionKey);

            Assert.Equal(0UL, reopened.FrameCount);
            Assert.Empty(reopened.ReadAllBodies());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A frame that fails part-way is cut away by the file itself, so the same instance stays appendable and readable.</summary>
    [Fact]
    public void AppendBodyFromSpool_FailingMidFrame_LeavesNoTornFrameBehind()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionKey = SessionKeyMaterial.CreateSessionKey();
            var turnId = SessionArchiveIds.NewArchiveTurnId();
            var big = new byte[250_000];
            new Random(3).NextBytes(big);
            using var spool = SessionBodySpool.Create(dir);
            Assert.True(spool.TryWrite(big));
            Assert.True(spool.TryComplete());
            var spoolPath = Directory.EnumerateFiles(dir, "*" + SessionBodySpool.FileExtension).Single();
            var spoolBytes = File.ReadAllBytes(spoolPath);
            spoolBytes[^30] ^= 0xFF; // inside the last chunk, so earlier chunks are written before the failure
            File.WriteAllBytes(spoolPath, spoolBytes);

            using var file = SessionFile.Create(path, SessionArchiveIds.NewArchiveSessionId(), sessionKey);
            file.AppendBody(0, SessionBodyKind.ClientRequest, "one"u8, turnId);
            var lengthBefore = file.Length;

            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
                () => file.AppendBodyFromSpool(0, SessionBodyKind.ClientResponse, spool, turnId));

            Assert.Equal(lengthBefore, file.Length);
            Assert.Equal(1UL, file.FrameCount);
            file.AppendBody(0, SessionBodyKind.ClientResponse, "two"u8, turnId);
            var frames = file.ReadAllBodies();
            Assert.Equal(["one", "two"], frames.Select(f => Encoding.UTF8.GetString(f.Plaintext!)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A file written in the retired version 1 layout is refused instead of misread as chunks.</summary>
    [Fact]
    public void Open_VersionOneFile_IsRefused()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "session.bin");
            var sessionId = SessionArchiveIds.NewArchiveSessionId();
            var header = new List<byte>("THSESS01"u8.ToArray());
            header.AddRange(sessionId.ToByteArray());
            header.AddRange([1, 0]);
            File.WriteAllBytes(path, header.ToArray());

            var exception = Assert.Throws<InvalidDataException>(
                () => SessionFile.Open(path, SessionKeyMaterial.CreateSessionKey(), sessionId));

            Assert.Contains("version 1", exception.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Creates a fresh, uniquely named directory under the system temp path for one test.</summary>
    /// <returns>The absolute path of the new directory; the caller deletes it.</returns>
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "thar-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
