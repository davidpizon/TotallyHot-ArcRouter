using System.Globalization;
using TotallyHot.ArcRouter.PriceCatalog;

namespace TotallyHot.ArcRouter.Proxy.Translation.ToolCalling;

/// <summary>
/// SQL access to the two tool-call capability tables (<c>provider_endpoint_capabilities</c> and
/// <c>model_tool_capabilities</c>), which live in the same <c>agent_telemetry.db</c> the price catalog
/// created (<c>docs/router/tool-call-normalization.md</c> Phase 1).
/// <para>
/// Depends on <see cref="PriceCatalogDatabase"/> purely as the owner of the connection string and schema
/// bootstrap - the naming is a historical artifact of that type being the repository's first database, and
/// its own summary already acknowledges the file is shared. Kept as a separate repository rather than more
/// methods on one of the price-catalog repositories (<see cref="PriceRepository"/>, and its five siblings
/// in that namespace) so pricing and tool-calling stay independently readable; they share a file, not a
/// domain.
/// </para>
/// </summary>
public sealed class ToolCallCapabilityRepository
{
    // Matches PriceCatalogRepositoryBase's own constant: a fixed-width, sortable, culture-invariant UTC form,
    // so a string comparison in SQL is also a chronological one.
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    private readonly PriceCatalogDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="ToolCallCapabilityRepository"/> class.</summary>
    /// <param name="database">Owns the connection string and schema for the shared SQLite file.</param>
    public ToolCallCapabilityRepository(PriceCatalogDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Reads every provider endpoint-capability row. Used to build the store's cache.</summary>
    public IReadOnlyList<ProviderEndpointCapabilities> GetProviderCapabilities()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT provider_key, openai_compatible, lmstudio_native, ollama_native,
                                     anthropic_compatible, json_schema_response_format, scanned_at_utc, scan_error
                              FROM provider_endpoint_capabilities;
                              """;

        var results = new List<ProviderEndpointCapabilities>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new ProviderEndpointCapabilities(
                ProviderKey: reader.GetString(0),
                OpenAiCompatible: reader.GetInt64(1) != 0,
                LmStudioNative: reader.GetInt64(2) != 0,
                OllamaNative: reader.GetInt64(3) != 0,
                AnthropicCompatible: reader.GetInt64(4) != 0,
                JsonSchemaResponseFormat: reader.GetInt64(5) != 0,
                ScannedAtUtc: ParseTimestamp(reader.GetString(6)),
                ScanError: reader.IsDBNull(7) ? null : reader.GetString(7)));

        return results;
    }

    /// <summary>Inserts or replaces one provider's endpoint capabilities.</summary>
    public void UpsertProviderCapabilities(ProviderEndpointCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO provider_endpoint_capabilities
                                  (provider_key, openai_compatible, lmstudio_native, ollama_native,
                                   anthropic_compatible, json_schema_response_format, scanned_at_utc, scan_error)
                              VALUES ($key, $openai, $lmstudio, $ollama, $anthropic, $jsonSchema, $scanned, $error)
                              ON CONFLICT(provider_key) DO UPDATE SET
                                  openai_compatible           = excluded.openai_compatible,
                                  lmstudio_native             = excluded.lmstudio_native,
                                  ollama_native               = excluded.ollama_native,
                                  anthropic_compatible        = excluded.anthropic_compatible,
                                  json_schema_response_format = excluded.json_schema_response_format,
                                  scanned_at_utc              = excluded.scanned_at_utc,
                                  scan_error                  = excluded.scan_error;
                              """;
        command.Parameters.AddWithValue(parameterName: "$key", value: capabilities.ProviderKey);
        command.Parameters.AddWithValue(parameterName: "$openai", value: capabilities.OpenAiCompatible ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$lmstudio", value: capabilities.LmStudioNative ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$ollama", value: capabilities.OllamaNative ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$anthropic", value: capabilities.AnthropicCompatible ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$jsonSchema",
            value: capabilities.JsonSchemaResponseFormat ? 1 : 0);
        command.Parameters.AddWithValue(parameterName: "$scanned", value: FormatTimestamp(capabilities.ScannedAtUtc));
        command.Parameters.AddWithValue(parameterName: "$error",
            value: (object?)capabilities.ScanError ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes one model's capability row, if present. Idempotent - deleting a row that was never written
    /// is a no-op rather than an error, so an operator clearing an override on a model that has simply
    /// never been classified behaves the same as clearing a real one.
    /// </summary>
    /// <param name="providerKey">The provider serving the model.</param>
    /// <param name="modelName">The client-facing model name.</param>
    public void DeleteModelCapability(string providerKey, string modelName)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              DELETE FROM model_tool_capabilities
                              WHERE provider_key = $provider AND model_name = $model;
                              """;
        command.Parameters.AddWithValue(parameterName: "$provider", value: providerKey);
        command.Parameters.AddWithValue(parameterName: "$model", value: modelName);
        command.ExecuteNonQuery();
    }

    /// <summary>Reads every model tool-capability row. Used to build the store's cache.</summary>
    public IReadOnlyList<ModelToolCapability> GetModelCapabilities()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT provider_key, model_name, dialect, confidence, evidence,
                                     observation_count, detected_at_utc
                              FROM model_tool_capabilities;
                              """;

        var results = new List<ModelToolCapability>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new ModelToolCapability(
                ProviderKey: reader.GetString(0),
                ModelName: reader.GetString(1),
                Dialect: reader.GetString(2),
                Confidence: ParseConfidence(reader.GetString(3)),
                Evidence: reader.IsDBNull(4) ? null : reader.GetString(4),
                ObservationCount: ReadObservationCount(reader.GetInt64(5)),
                DetectedAtUtc: ParseTimestamp(reader.GetString(6))));

        return results;
    }

    /// <summary>
    /// Writes a model's capability, but only when its confidence is at least the stored row's.
    /// </summary>
    /// <remarks>
    /// The ranking gate lives in SQL rather than in a read-then-write in the store, so two concurrent
    /// requests classifying the same model cannot interleave into a lost update - the same reason the price
    /// catalog puts its <c>priority_score &gt;=</c> gate in the upsert's WHERE clause instead of in C#.
    /// <c>&gt;=</c> rather than <c>&gt;</c> so a fresh observation can correct an earlier one of equal
    /// standing (a model reloaded with a different template), while still leaving
    /// <see cref="DetectionConfidence.Operator"/> unreachable by any automatic tier.
    /// <para>
    /// <b><c>observation_count</c> is incremented in SQL, not supplied by the caller</b>, for that same
    /// reason. A read-then-write in C# lets two concurrent responses for the same model both read the old
    /// count and both write the same successor, silently undercounting - and the window is not
    /// hypothetical, since the observations that matter all happen on the *first* requests for a model not
    /// yet classified, which is precisely when several can be in flight at once. Nothing reads this number
    /// today - Phase 5 selected emulation from the chat template rather than from a count - so it exists for
    /// a future diagnostic, and handing that a counter which quietly loses updates would be handing it a bug.
    /// </para>
    /// <para>
    /// The increment applies only to a <see cref="DetectionConfidence.Observed"/> write, which is the one
    /// confidence that means "a live response was classified". A template or model-id write is metadata
    /// read off the provider, not an observation of anything, so it leaves the counter where it stands
    /// rather than resetting it - <paramref name="capability"/>'s own
    /// <see cref="ModelToolCapability.ObservationCount"/> seeds the row only when it is being inserted.
    /// </para>
    /// </remarks>
    /// <param name="capability">The classification to persist.</param>
    /// <returns>
    /// <see langword="true"/> when the row was written; <see langword="false"/> when an
    /// existing, higher-confidence row was left untouched.
    /// </returns>
    public bool TryUpsertModelCapability(ModelToolCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO model_tool_capabilities
                                  (provider_key, model_name, dialect, confidence, evidence, observation_count, detected_at_utc)
                              VALUES ($provider, $model, $dialect, $confidence, $evidence, $count, $detected)
                              ON CONFLICT(provider_key, model_name) DO UPDATE SET
                                  dialect           = excluded.dialect,
                                  confidence        = excluded.confidence,
                                  evidence          = excluded.evidence,
                                  observation_count = model_tool_capabilities.observation_count + $increment,
                                  detected_at_utc   = excluded.detected_at_utc
                              WHERE $rank >= (
                                  SELECT CASE model_tool_capabilities.confidence
                                      WHEN 'operator'  THEN 3
                                      WHEN 'observed'  THEN 2
                                      WHEN 'template'  THEN 1
                                      WHEN 'heuristic' THEN 0
                                      ELSE 0
                                  END);
                              """;
        command.Parameters.AddWithValue(parameterName: "$provider", value: capability.ProviderKey);
        command.Parameters.AddWithValue(parameterName: "$model", value: capability.ModelName);
        command.Parameters.AddWithValue(parameterName: "$dialect", value: capability.Dialect);
        command.Parameters.AddWithValue(parameterName: "$confidence", value: FormatConfidence(capability.Confidence));
        command.Parameters.AddWithValue(parameterName: "$evidence",
            value: (object?)capability.Evidence ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$count", value: capability.ObservationCount);
        command.Parameters.AddWithValue(parameterName: "$detected", value: FormatTimestamp(capability.DetectedAtUtc));
        command.Parameters.AddWithValue(parameterName: "$rank", value: (int)capability.Confidence);

        // Only a live response bumps the counter, and SQLite - not the caller - does the bumping. See this
        // method's remarks; DetectionConfidence.Observed is produced by exactly one writer,
        // ToolCallObservationRecorder, and only ever from a response that actually carried a tool call.
        command.Parameters.AddWithValue(
            parameterName: "$increment",
            value: capability.Confidence == DetectionConfidence.Observed ? 1 : 0);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Reads every probed context-window row. Used to build the store's cache.</summary>
    /// <remarks>
    /// Rows whose stored length cannot be represented as a positive <see cref="int"/> are skipped rather
    /// than clamped - the opposite of <see cref="ReadObservationCount"/>'s treatment of a corrupt count,
    /// and deliberately so. A clamped count is still a plausible count, but a clamped context window would
    /// be advertised to clients as a real limit and used to size prompts. "Unknown" is the honest reading
    /// of an unusable value here, and absence is exactly how this feature represents unknown.
    /// </remarks>
    public IReadOnlyList<ModelContextWindow> GetModelContextWindows()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT provider_key, model_name, context_length, architecture, evidence, detected_at_utc
                              FROM model_context_windows;
                              """;

        var results = new List<ModelContextWindow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var length = ReadContextLength(reader.GetInt64(2));
            if (length is null) continue;

            results.Add(new ModelContextWindow(
                ProviderKey: reader.GetString(0),
                ModelName: reader.GetString(1),
                ContextLength: length.Value,
                Architecture: reader.IsDBNull(3) ? null : reader.GetString(3),
                Evidence: reader.IsDBNull(4) ? null : reader.GetString(4),
                DetectedAtUtc: ParseTimestamp(reader.GetString(5))));
        }

        return results;
    }

    /// <summary>
    /// Writes a model's probed context window, overwriting any existing row unconditionally.
    /// </summary>
    /// <remarks>
    /// No confidence gate, unlike <see cref="TryUpsertModelCapability"/> - this mirrors
    /// <see cref="UpsertProviderCapabilities"/> instead. A confidence ladder ranks *how* a classification
    /// was learned so a filename guess cannot overwrite a template read; a context window has no such
    /// ranking to build. Ollama's <c>model_info</c> and LM Studio's <c>max_context_length</c> are peers,
    /// and nothing guesses a window from a model id. A gate would also be actively harmful: a model
    /// reloaded under a different <c>num_ctx</c> genuinely has a different window, and a <c>&gt;=</c> gate
    /// would freeze the first reading forever.
    /// <para>
    /// The invariant that keeps last-write-wins safe lives one layer up, in the probe: a scan that read
    /// nothing returns no window and so never reaches this method, meaning a failed re-probe can never
    /// clear a value that was previously known.
    /// </para>
    /// </remarks>
    /// <param name="window">The window to persist.</param>
    public void UpsertModelContextWindow(ModelContextWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO model_context_windows
                                  (provider_key, model_name, context_length, architecture, evidence, detected_at_utc)
                              VALUES ($provider, $model, $length, $architecture, $evidence, $detected)
                              ON CONFLICT(provider_key, model_name) DO UPDATE SET
                                  context_length  = excluded.context_length,
                                  architecture    = excluded.architecture,
                                  evidence        = excluded.evidence,
                                  detected_at_utc = excluded.detected_at_utc;
                              """;
        command.Parameters.AddWithValue(parameterName: "$provider", value: window.ProviderKey);
        command.Parameters.AddWithValue(parameterName: "$model", value: window.ModelName);
        command.Parameters.AddWithValue(parameterName: "$length", value: window.ContextLength);
        command.Parameters.AddWithValue(parameterName: "$architecture",
            value: (object?)window.Architecture ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$evidence", value: (object?)window.Evidence ?? DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$detected", value: FormatTimestamp(window.DetectedAtUtc));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads a stored context length, or <see langword="null"/> when it is not a usable positive
    /// <see cref="int"/>. See <see cref="GetModelContextWindows"/> for why this rejects rather than clamps.
    /// </summary>
    private static int? ReadContextLength(long stored)
    {
        return stored switch
        {
            <= 0 => null,
            > int.MaxValue => null,
            _ => (int)stored
        };
    }

    /// <summary>
    /// Clamps a stored observation count into the non-negative <see cref="int"/> range.
    /// </summary>
    /// <remarks>
    /// SQLite's INTEGER is 64-bit, so a hand-edited or corrupted row can hold a value no <see cref="int"/>
    /// can represent. A plain <c>(int)</c> cast is unchecked in C# and wraps silently, so a large positive
    /// count would read back as a negative one - a count that is meaningless on its face and that any
    /// future reader of it would misread. Clamping keeps
    /// this consistent with how <see cref="ParseTimestamp"/> and <see cref="ParseConfidence"/> already treat
    /// unparseable stored values: degrade to something sane rather than take the proxy down on a read.
    /// </remarks>
    private static int ReadObservationCount(long stored)
    {
        return stored switch
        {
            < 0 => 0,
            > int.MaxValue => int.MaxValue,
            _ => (int)stored
        };
    }

    /// <summary>Formats a timestamp into the sortable invariant UTC form the schema stores.</summary>
    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(format: TimestampFormat, provider: CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses a stored timestamp back to UTC. Falls back to <see cref="DateTimeOffset.MinValue"/> rather
    /// than throwing on an unparseable value: a hand-edited row should degrade to "very old" (and so be
    /// rescanned) rather than take the proxy down on its first read.
    /// </summary>
    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.TryParse(
            input: value,
            formatProvider: CultureInfo.InvariantCulture,
            styles: DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            result: out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    /// <summary>Formats a confidence as the lowercase token the schema stores.</summary>
    private static string FormatConfidence(DetectionConfidence confidence)
    {
        return confidence.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Parses a stored confidence token. An unrecognized value degrades to the weakest tier so any real
    /// detection can correct it, rather than reading as an unoverwritable <c>operator</c> by accident.
    /// </summary>
    private static DetectionConfidence ParseConfidence(string value)
    {
        return Enum.TryParse<DetectionConfidence>(value: value, true, result: out var parsed)
            ? parsed
            : DetectionConfidence.Heuristic;
    }
}