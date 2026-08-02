using TotallyHot.ArcRouter.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// An implementation of <see cref="IRouterMemoryStore"/> that persists memory to a JSON file.
/// </summary>
public class JsonRouterMemoryStore : IRouterMemoryStore
{
    private readonly ILogger<JsonRouterMemoryStore> _logger;
    private readonly string _filePath;
    private static readonly JsonSerializerOptions _serializerOptions = new() { WriteIndented = true };

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonRouterMemoryStore"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="options">The routing options containing the file path.</param>
    public JsonRouterMemoryStore(ILogger<JsonRouterMemoryStore> logger, IOptions<RoutingOptions> options)
    {
        _logger = logger;

        var configuredPath = options.Value.MemoryPath;
        _filePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);

        _logger.LogInformation("JSON memory store will use file: {FilePath}", _filePath);
    }

    /// <inheritdoc />
    public async Task<ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>> LoadAsync()
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("Memory file not found. Starting with empty memory.");
            return new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var memory = JsonSerializer.Deserialize<ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>>(json);
            _logger.LogInformation("Successfully loaded memory from {FilePath}", _filePath);
            return memory ?? new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load memory from {FilePath}. Starting with empty memory.", _filePath);
            return new ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>> memory)
    {
        try
        {
            var directoryPath = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var json = JsonSerializer.Serialize(memory, _serializerOptions);
            await File.WriteAllTextAsync(_filePath, json);
            _logger.LogInformation("Successfully saved memory to {FilePath}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save memory to {FilePath}", _filePath);
        }
    }
}

