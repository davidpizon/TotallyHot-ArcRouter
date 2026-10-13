namespace TotallyHot.ArcRouter.Tests.Sessions.Fixtures;

/// <summary>Covers <see cref="ConversationZipFixtureConverter"/> including the outside-repo guard.</summary>
public sealed class ConversationZipFixtureConverterTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "thar-conv-" + Guid.NewGuid().ToString("N"));

    /// <summary>Initializes a new instance of the <see cref="ConversationZipFixtureConverterTests"/> class.</summary>
    public ConversationZipFixtureConverterTests() => Directory.CreateDirectory(_work);

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

    /// <summary>Extracting outside a fake repo root preserves every entry's bytes.</summary>
    [Fact]
    public void Convert_OutsideRepoRoot_PreservesEntryBytes()
    {
        var fakeRepo = Path.Combine(_work, "repo");
        Directory.CreateDirectory(fakeRepo);
        var response = """{"choices":[{"message":{"content":"copied"}}]}"""u8.ToArray();
        var zip = SyntheticConversationZip.Write(response, directory: Path.Combine(_work, "zip"));
        var destination = Path.Combine(_work, "fixtures-out");

        ConversationZipFixtureConverter.Convert(zip, destination, repositoryRoot: fakeRepo);

        Assert.True(File.Exists(Path.Combine(destination, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(destination, "turns.jsonl")));
        var responseFiles = Directory.GetFiles(destination, "*.response.json", SearchOption.AllDirectories);
        Assert.Single(responseFiles);
        Assert.Equal(response, File.ReadAllBytes(responseFiles[0]));
    }

    /// <summary>A destination under the repository root is refused before any write.</summary>
    [Fact]
    public void Convert_DestinationUnderRepoRoot_IsRefused()
    {
        var fakeRepo = Path.Combine(_work, "repo");
        Directory.CreateDirectory(fakeRepo);
        var zip = SyntheticConversationZip.Write("{\"ok\":true}"u8.ToArray(), directory: Path.Combine(_work, "zip2"));
        var destination = Path.Combine(fakeRepo, "src", "fixtures");

        var ex = Assert.Throws<ConversationZipException>(() =>
            ConversationZipFixtureConverter.Convert(zip, destination, repositoryRoot: fakeRepo));

        Assert.Contains("repository root", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destination));
    }

    /// <summary><see cref="ConversationZipFixtureConverter.IsUnderOrEqual"/> treats the folder itself as inside.</summary>
    [Fact]
    public void IsUnderOrEqual_FolderItself_IsTrue()
    {
        var folder = Path.Combine(_work, "root");
        Directory.CreateDirectory(folder);
        Assert.True(ConversationZipFixtureConverter.IsUnderOrEqual(folder, folder));
    }
}
