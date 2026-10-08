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
            var request = Encoding.UTF8.GetBytes("""{"messages":[{"role":"user","content":"hello"}]}""");
            var response = Encoding.UTF8.GetBytes("""{"content":[{"type":"text","text":"hi"}]}""");

            using (var file = SessionFile.Create(path, archiveSessionId, (byte[])sessionKey.Clone()))
            {
                file.AppendBody(0, SessionBodyKind.ClientRequest, request, archiveTurnId);
                file.AppendBody(0, SessionBodyKind.ClientResponse, response, archiveTurnId);
            }

            using var reopened = SessionFile.Open(path, sessionKey, archiveSessionId);
            var frames = reopened.ReadAllBodies();

            Assert.Equal(2, frames.Count);
            Assert.Equal(request, frames[0].Plaintext);
            Assert.Equal(response, frames[1].Plaintext);
            Assert.Equal(archiveTurnId, frames[0].ArchiveTurnId);
            Assert.Equal(SessionBodyKind.ClientRequest, frames[0].Kind);
            Assert.Equal(SessionBodyKind.ClientResponse, frames[1].Kind);
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

    /// <summary>Wrap then unwrap recovers the same session key under a master key.</summary>
    [Fact]
    public void WrapAndUnwrap_RoundTripsSessionKey()
    {
        var master = SessionKeyMaterial.CreateMasterKey();
        var session = SessionKeyMaterial.CreateSessionKey();

        var wrapped = SessionKeyMaterial.WrapSessionKey(master, session);
        var unwrapped = SessionKeyMaterial.UnwrapSessionKey(master, wrapped);

        Assert.Equal(session, unwrapped);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "thar-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
