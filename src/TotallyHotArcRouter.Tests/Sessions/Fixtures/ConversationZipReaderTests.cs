using System.Text;
using TotallyHot.ArcRouter.Sessions.Export;

namespace TotallyHot.ArcRouter.Tests.Sessions.Fixtures;

/// <summary>Covers <see cref="ConversationZipReader"/> against synthetic schema-v1 zips.</summary>
public sealed class ConversationZipReaderTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "thar-reader-" + Guid.NewGuid().ToString("N"));

    /// <summary>Initializes a new instance of the <see cref="ConversationZipReaderTests"/> class.</summary>
    public ConversationZipReaderTests() => Directory.CreateDirectory(_work);

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp fixtures.
        }
    }

    /// <summary>A valid synthetic zip yields metadata and request/response bytes unchanged.</summary>
    [Fact]
    public void ReadAll_SyntheticZip_ReturnsBodiesAndMetadata()
    {
        var response = """{"choices":[{"message":{"content":"hi"}}],"usage":{"prompt_tokens":1,"completion_tokens":2}}"""u8.ToArray();
        var zip = SyntheticConversationZip.Write(response, directory: Path.Combine(_work, "ok"));

        var turn = Assert.Single(ConversationZipReader.ReadAll(zip));

        Assert.Equal(SyntheticConversationZip.DefaultRequest, turn.ClientRequest);
        Assert.Equal(response, turn.ClientResponse);
        Assert.NotNull(turn.MetadataJson);
        Assert.Contains("\"provider\":\"openai\"", turn.MetadataJson, StringComparison.Ordinal);
        Assert.Null(turn.ProviderRequest);
        Assert.Null(turn.ProviderResponse);
    }

    /// <summary>An unsupported schema version is refused with a message that names the version.</summary>
    [Fact]
    public void ReadAll_UnsupportedSchemaVersion_NamesTheVersion()
    {
        var good = SyntheticConversationZip.Write("{\"ok\":true}"u8.ToArray(), directory: Path.Combine(_work, "ver-src"));
        var bad = Path.Combine(_work, "bad-version.zip");
        SyntheticConversationZip.RewriteSchemaVersion(good, bad, schemaVersion: 99);

        var ex = Assert.Throws<ConversationZipException>(() => ConversationZipReader.ReadAll(bad));

        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
        Assert.Contains(ConversationExportWriter.SchemaVersion.ToString(), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A turns.jsonl whose hash no longer matches the manifest is refused.</summary>
    [Fact]
    public void ReadAll_MismatchedTurnsHash_IsRefused()
    {
        var good = SyntheticConversationZip.Write("{\"ok\":true}"u8.ToArray(), directory: Path.Combine(_work, "hash-src"));
        var bad = Path.Combine(_work, "bad-hash.zip");
        SyntheticConversationZip.CorruptTurnsHash(good, bad);

        var ex = Assert.Throws<ConversationZipException>(() => ConversationZipReader.ReadAll(bad));

        Assert.Contains("turns.jsonl", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A zip-slip entry name is refused before any turn is yielded.</summary>
    [Fact]
    public void ReadAll_ZipSlipEntry_IsRefused()
    {
        var good = SyntheticConversationZip.Write("{\"ok\":true}"u8.ToArray(), directory: Path.Combine(_work, "slip-src"));
        var bad = Path.Combine(_work, "slip.zip");
        SyntheticConversationZip.AddUnsafeEntry(good, bad, "../evil.txt");

        var ex = Assert.Throws<ConversationZipException>(() => ConversationZipReader.ReadAll(bad));

        Assert.Contains("not allowed", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Reading a missing path throws <see cref="FileNotFoundException"/>.</summary>
    [Fact]
    public void ReadAll_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ConversationZipReader.ReadAll(Path.Combine(_work, "no-such.zip")));
    }

    /// <summary>Smoke: encoding round-trip keeps non-ASCII request bytes.</summary>
    [Fact]
    public void ReadAll_NonAsciiRequest_RoundTripsExactly()
    {
        var request = Encoding.UTF8.GetBytes("""{"messages":[{"role":"user","content":"café ⌘"}]}""");
        var zip = SyntheticConversationZip.Write(
            clientResponse: """{"choices":[{"message":{"content":"ok"}}]}"""u8.ToArray(),
            clientRequest: request,
            directory: Path.Combine(_work, "unicode"));

        var turn = Assert.Single(ConversationZipReader.ReadAll(zip));

        Assert.Equal(request, turn.ClientRequest);
    }
}
