using System.Security.Cryptography;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Storage;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Holds the single 256-bit response-vector key. It is read once and kept in memory. When the secret is
/// missing, every <c>response_vectors</c> row is deleted and a new key is written: that is what a shred
/// leaves behind, and it is also the first-run path when the table is empty.
/// </summary>
public sealed class ResponseVectorKeyHolder
{
    private readonly RouterMemoryDatabase _database;
    private readonly ILogger<ResponseVectorKeyHolder> _logger;
    private readonly ISecretReader _reader;
    private readonly ISecretWriter _writer;
    private readonly Lock _gate = new();
    private byte[]? _key;

    /// <summary>Initializes a new instance of the <see cref="ResponseVectorKeyHolder"/> class.</summary>
    /// <param name="reader">Reads the key from <c>secrets.dat</c>.</param>
    /// <param name="writer">Stores a newly generated key.</param>
    /// <param name="database">The memory database whose <c>response_vectors</c> rows a missing key invalidates.</param>
    /// <param name="logger">The logger.</param>
    public ResponseVectorKeyHolder(
        ISecretReader reader,
        ISecretWriter writer,
        RouterMemoryDatabase database,
        ILogger<ResponseVectorKeyHolder> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _reader = reader;
        _writer = writer;
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Returns the cached key, reading it from the secret store on the first call. A missing or unreadable
    /// secret deletes every response vector and starts a new key.
    /// </summary>
    /// <returns>The 32-byte key. The caller must not mutate it.</returns>
    public byte[] GetOrCreate()
    {
        lock (_gate)
        {
            if (_key is not null) return _key;

            if (_reader.TryRead(name: ResponseVectorSeal.SecretName, value: out var stored) &&
                TryDecode(stored, out var existing))
            {
                _key = existing;
                return _key;
            }

            DeleteAllVectors();
            _key = RandomNumberGenerator.GetBytes(count: 32);
            _writer.Write(name: ResponseVectorSeal.SecretName, value: Convert.ToBase64String(_key));
            _logger.LogInformation("Started a new response-vector key and deleted any rows the previous key sealed.");
            return _key;
        }
    }

    /// <summary>Decodes a base64 32-byte key. Anything else is treated as a missing key.</summary>
    private static bool TryDecode(string stored, out byte[] key)
    {
        key = [];
        try
        {
            var decoded = Convert.FromBase64String(stored);
            if (decoded.Length != 32) return false;
            key = decoded;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Deletes every response vector. Does not need the key: the ciphertext is being discarded.</summary>
    private void DeleteAllVectors()
    {
        _database.EnsureCreated();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM response_vectors;";
        command.ExecuteNonQuery();
        if (!SqliteHardening.TruncateWal(connection))
            _logger.LogWarning(
                "Response-vector deletion is not yet final: the write-ahead log was busy and will be truncated at the next start.");
    }
}
