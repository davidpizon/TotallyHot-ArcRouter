using System.Globalization;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Reads and writes the <c>router_settings</c> key/value table
/// (docs/router/self-organizing-classification-plan.md Phase T6): the mutable, operator-facing overrides
/// backing <see cref="RouterSettingsAdminGrpcService"/>'s Save action and, through
/// <see cref="RouterSettingsConfigureOptions"/>, <see cref="Models.RoutingOptions"/> itself. A row's mere
/// presence means "overridden"; its absence falls through to whatever <c>appsettings.json</c> or the coded
/// default already produced - see <see cref="RouterSettingsConfigureOptions"/>'s remarks for the full
/// precedence chain.
/// </summary>
/// <remarks>
/// Deliberately owns its own <see cref="RouterMemoryDatabase"/> instance rather than taking the
/// DI-registered singleton: that singleton is constructed from <c>IOptions&lt;RoutingOptions&gt;</c>, and
/// this store is itself a dependency of <see cref="RouterSettingsConfigureOptions"/> - one of the
/// <c>IConfigureOptions&lt;RoutingOptions&gt;</c> steps that <em>produce</em> <c>RoutingOptions</c>. Resolving
/// the DI singleton here would ask the options system to finish building <c>RoutingOptions</c> in order to
/// start building it, a circular dependency the options pipeline cannot resolve. Building a private,
/// unshared <see cref="RouterMemoryDatabase"/> from the same resolved file path breaks the cycle - SQLite's
/// WAL mode supports multiple connections to one file, so a second <see cref="RouterMemoryDatabase"/> wrapper
/// over the same path is safe, just an extra thin object.
/// </remarks>
public sealed class RouterSettingsStore
{
    /// <summary>
    /// The <c>router_settings</c> key for <see cref="Models.RoutingOptions.EnableAdaptiveRouting"/>, stored as
    /// <c>"0"</c>/<c>"1"</c>.
    /// </summary>
    public const string AdaptiveRoutingEnabledKey = "AdaptiveRoutingEnabled";

    /// <summary>
    /// The <c>router_settings</c> key for <see cref="Models.RoutingOptions.EmbeddingMemoryCapacity"/>, stored as its
    /// base-10 integer string.
    /// </summary>
    public const string EmbeddingMemoryCapacityKey = "EmbeddingMemoryCapacity";

    /// <summary>The <c>router_settings</c> key for <see cref="Judge.JudgeOptions.Enabled"/>, stored as <c>"0"</c>/<c>"1"</c>.</summary>
    public const string JudgeEnabledKey = "JudgeEnabled";

    /// <summary>
    /// The <c>router_settings</c> key for <see cref="Judge.JudgeOptions.ModelName"/>, stored as the
    /// client-facing model name verbatim. An empty stored value means "automatic" just as an absent row
    /// does - the operator explicitly choosing automatic and never having chosen at all are the same state.
    /// </summary>
    public const string JudgeModelNameKey = "JudgeModelName";

    /// <summary>
    /// The <c>router_settings</c> key for <see cref="Transcripts.TranscriptOptions.Enabled"/>, stored as <c>"0"</c>/
    /// <c>"1"</c>.
    /// </summary>
    public const string TranscriptCaptureEnabledKey = "TranscriptCaptureEnabled";

    /// <summary>
    /// The <c>router_settings</c> key for <see cref="Judge.PortfolioGraderOptions.CodeJudgeEnabled"/>, stored
    /// as <c>"0"</c>/<c>"1"</c>.
    /// </summary>
    public const string CodeJudgeEnabledKey = "CodeJudgeEnabled";

    /// <summary>
    /// The <c>router_settings</c> key for <see cref="Judge.PortfolioGraderOptions.IceScoreEnabled"/>, stored
    /// as <c>"0"</c>/<c>"1"</c>.
    /// </summary>
    public const string IceScoreEnabledKey = "IceScoreEnabled";

    /// <summary>
    /// The <c>router_settings</c> key for <see cref="Judge.PortfolioGraderOptions.RaceEnabled"/>, stored as
    /// <c>"0"</c>/<c>"1"</c>.
    /// </summary>
    public const string RaceEnabledKey = "RaceEnabled";

    private readonly RouterMemoryDatabase _database;
    private readonly ILogger<RouterSettingsStore> _logger;

    /// <summary>Initializes a new instance of the <see cref="RouterSettingsStore"/> class.</summary>
    /// <param name="database">
    /// The database wrapper this store reads/writes through. Its schema (including <c>router_settings</c>)
    /// is ensured eagerly here rather than relying on another component's startup ordering, since this
    /// store's callers - notably <see cref="RouterSettingsConfigureOptions"/> - may run before
    /// <c>StartupHealthCheckHostedService</c>'s own <see cref="RouterMemoryDatabase.EnsureCreated"/> call.
    /// </param>
    /// <param name="logger">The logger.</param>
    public RouterSettingsStore(RouterMemoryDatabase database, ILogger<RouterSettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);

        _database = database;
        _logger = logger;
        _database.EnsureCreated();
    }

    /// <summary>Reads a stored boolean override, if one has been set.</summary>
    /// <param name="key">The setting key, e.g. <see cref="AdaptiveRoutingEnabledKey"/>.</param>
    /// <param name="value">The stored value, when present.</param>
    /// <returns><see langword="true"/> if a row exists for <paramref name="key"/>; otherwise <see langword="false"/>.</returns>
    public bool TryGetBool(string key, out bool value)
    {
        if (TryGetRaw(key: key, value: out var raw) && raw is "1" or "0")
        {
            value = raw == "1";
            return true;
        }

        value = false;
        return false;
    }

    /// <summary>Reads a stored integer override, if one has been set.</summary>
    /// <param name="key">The setting key, e.g. <see cref="EmbeddingMemoryCapacityKey"/>.</param>
    /// <param name="value">The stored value, when present and parseable.</param>
    /// <returns>
    /// <see langword="true"/> if a row exists for <paramref name="key"/> and parses as an integer; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public bool TryGetInt(string key, out int value)
    {
        if (TryGetRaw(key: key, value: out var raw) && int.TryParse(s: raw, style: NumberStyles.Integer,
                provider: CultureInfo.InvariantCulture, result: out var parsed))
        {
            value = parsed;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>Reads a stored string override, if one has been set.</summary>
    /// <param name="key">The setting key, e.g. <see cref="JudgeModelNameKey"/>.</param>
    /// <param name="value">The stored value, when present; <see cref="string.Empty"/> otherwise.</param>
    /// <returns><see langword="true"/> if a row exists for <paramref name="key"/>; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Unlike <see cref="TryGetBool"/> and <see cref="TryGetInt"/> this cannot fail to parse, so a present
    /// row always reports <see langword="true"/> - including one holding the empty string, which callers
    /// are free to treat as its own meaningful value rather than as an absent override.
    /// </remarks>
    public bool TryGetString(string key, out string value)
    {
        return TryGetRaw(key: key, value: out value);
    }

    /// <summary>Persists a boolean override, replacing any prior value for <paramref name="key"/>.</summary>
    public void SetBool(string key, bool value)
    {
        SetRaw(key: key, value: value ? "1" : "0");
    }

    /// <summary>Persists a string override, replacing any prior value for <paramref name="key"/>.</summary>
    public void SetString(string key, string value)
    {
        SetRaw(key: key, value: value);
    }

    /// <summary>Persists an integer override, replacing any prior value for <paramref name="key"/>.</summary>
    public void SetInt(string key, int value)
    {
        SetRaw(key: key, value: value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Reads a raw stored value by key.</summary>
    private bool TryGetRaw(string key, out string value)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM router_settings WHERE key = $key;";
        command.Parameters.AddWithValue(parameterName: "$key", value: key);

        var result = command.ExecuteScalar();
        if (result is string raw)
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Upserts a raw value by key.</summary>
    private void SetRaw(string key, string value)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO router_settings (key, value) VALUES ($key, $value)
                              ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                              """;
        command.Parameters.AddWithValue(parameterName: "$key", value: key);
        command.Parameters.AddWithValue(parameterName: "$value", value: value);
        command.ExecuteNonQuery();

        _logger.LogInformation(message: "Router setting {Key} updated to {Value}.", key, value);
    }
}