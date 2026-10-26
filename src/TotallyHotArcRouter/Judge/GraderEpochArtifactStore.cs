using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TotallyHot.ArcRouter.PriceCatalog;
using TotallyHot.ArcRouter.Router;

namespace TotallyHot.ArcRouter.Judge;

/// <summary>
/// The on-disk phase 7 grader epoch. One directory per kind under
/// <see cref="StorageOptions.ResolveGraderEpochDirectory"/>. <c>accepted.json</c> is the pointer and is
/// replaced last, by a temp file in that kind directory and a rename, so a reader never sees a
/// half-written pointer. Rollback clears the pointer and does not promote a previous file. In phase 7
/// the teacher never loses authority.
/// </summary>
public sealed class GraderEpochArtifactStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly ResponseVectorKeyHolder _keys;
    private readonly ILogger<GraderEpochArtifactStore> _logger;
    private readonly IOptionsMonitor<StorageOptions> _storage;

    /// <summary>Initializes a new instance of the <see cref="GraderEpochArtifactStore"/> class.</summary>
    /// <param name="storage">Supplies the epoch directory. The logreg file is a different path.</param>
    /// <param name="keys">The vector key the reservoir opens with. Response blobs use a different associated-data label.</param>
    /// <param name="logger">Logs rollback, retirement's caller does its own line, and reservoir authentication failures.</param>
    public GraderEpochArtifactStore(
        IOptionsMonitor<StorageOptions> storage,
        ResponseVectorKeyHolder keys,
        ILogger<GraderEpochArtifactStore> logger)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);
        _storage = storage;
        _keys = keys;
        _logger = logger;
    }

    /// <summary>
    /// Clears the accepted pointer for <paramref name="kind"/>. Does not throw when nothing is accepted,
    /// does not promote a previous file, and does not delete retired files. Shadow scores stay off until
    /// a later fit clears the gate. Phase 8 calls this same method.
    /// </summary>
    /// <param name="kind"><see cref="GraderEpochKinds.Code"/> or <see cref="GraderEpochKinds.NonCode"/>.</param>
    public void RollbackToTeacher(string kind)
    {
        lock (_gate)
        {
            var artifact = ReadAcceptedLocked(kind);
            ClearAcceptedLocked(kind);
            if (artifact is null)
            {
                _logger.LogInformation(
                    message:
                    "Grader epoch rollback for kind {Kind} found no accepted artifact. The teacher is already the grade of record.",
                    kind);
                return;
            }

            _logger.LogInformation(
                message:
                "Grader epoch cleared the accepted pointer for kind {Kind}. Artifact {ArtifactId}, previous artifact {PreviousArtifactId}. Shadow scores stay off until a new artifact is accepted.",
                kind,
                artifact.ArtifactId,
                Marker(artifact.PreviousArtifactId));
        }
    }

    /// <summary>The accepted artifact, or null when the pointer is absent or the file cannot be read.</summary>
    /// <param name="kind">The kind folder.</param>
    /// <returns>The accepted artifact.</returns>
    internal GraderEpochArtifact? TryReadAccepted(string kind)
    {
        lock (_gate) return ReadAcceptedLocked(kind);
    }

    /// <summary>
    /// Clears the pointer when the accepted artifact's teacher model, prompt version, BGE id, or feature
    /// schema does not match. Files stay for audit. The retired id is not installed as the next
    /// previous-artifact id, because the pointer is gone.
    /// </summary>
    /// <param name="kind">The kind folder.</param>
    /// <param name="teacherModel">The current teacher model.</param>
    /// <param name="promptVersion">The current teacher prompt version.</param>
    /// <param name="bgeModelId">The current BGE identity.</param>
    /// <param name="featureSchemaVersion">The schema version this process fits.</param>
    /// <param name="artifactId">The retired id when this returns a field name.</param>
    /// <param name="field">Which field differed, or null when the artifact still matches.</param>
    /// <returns><see langword="true"/> when the pointer was cleared.</returns>
    internal bool TryRetireMismatch(string kind, string teacherModel, string promptVersion, string bgeModelId,
        int featureSchemaVersion, out string? artifactId, out string? field)
    {
        lock (_gate)
        {
            artifactId = null;
            field = null;
            var artifact = ReadAcceptedLocked(kind);
            if (artifact is null) return false;

            field = Mismatch(artifact, teacherModel, promptVersion, bgeModelId, featureSchemaVersion);
            if (field is null) return false;

            artifactId = artifact.ArtifactId;
            ClearAcceptedLocked(kind);
            return true;
        }
    }

    /// <summary>
    /// Writes the candidate as the accepted artifact. <c>accepted.json</c> is replaced last. A comparable
    /// predecessor's files stay where they are; its id is already on <paramref name="artifact"/>.
    /// </summary>
    /// <param name="artifact">The candidate that cleared the gate. Status is <c>accepted, shadow</c>.</param>
    /// <param name="reservoir">The sealed reservoir file.</param>
    internal void CommitAccepted(GraderEpochArtifact artifact, byte[] reservoir)
    {
        lock (_gate)
        {
            var directory = PrepareKind(artifact.Kind);
            try
            {
                WritePair(directory: directory, artifact: artifact, reservoir: reservoir, failed: false);
                ReplaceAtomically(directory: directory, fileName: "accepted.json",
                    bytes: JsonSerializer.SerializeToUtf8Bytes(new AcceptedPointer { ArtifactId = artifact.ArtifactId },
                        Json));
                _cache.Remove(artifact.Kind);
            }
            catch
            {
                DeleteTempsLocked(artifact.Kind);
                throw;
            }
        }
    }

    /// <summary>
    /// Files a candidate that was fit and failed the gate. <c>accepted.json</c> is not created or
    /// replaced. Shadow scoring does not load <c>failed</c>. Oldest failed ids beyond the retention
    /// cap are deleted; accepted files are not in that cap.
    /// </summary>
    /// <param name="artifact">The candidate. Its status is not <c>accepted, shadow</c>.</param>
    /// <param name="reservoir">The sealed reservoir, kept as a diagnostic.</param>
    /// <param name="retention">How many failed candidates to keep for this kind.</param>
    internal void CommitFailed(GraderEpochArtifact artifact, byte[] reservoir, int retention)
    {
        lock (_gate)
        {
            var directory = PrepareKind(artifact.Kind);
            try
            {
                WritePair(directory: directory, artifact: artifact, reservoir: reservoir, failed: true);
                PruneFailed(directory, retention);
            }
            catch
            {
                DeleteTempsLocked(artifact.Kind);
                throw;
            }
        }
    }

    /// <summary>Deletes <c>*.tmp</c> in the kind directory so a thrown fit cannot leave a temp as the pointer.</summary>
    /// <param name="kind">The kind folder.</param>
    internal void DeleteTemps(string kind)
    {
        lock (_gate) DeleteTempsLocked(kind);
    }

    /// <summary>
    /// Opens the accepted artifact's reservoir. A failed authentication drops that entry, logs the
    /// artifact id and the source correlation id, and does not substitute a zero vector.
    /// </summary>
    /// <param name="artifact">The accepted artifact.</param>
    /// <returns>The question vectors that opened, in reservoir-sample order.</returns>
    internal float[][] OpenReservoir(GraderEpochArtifact artifact)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(artifact.Kind, out var cached) &&
                string.Equals(cached.ArtifactId, artifact.ArtifactId, StringComparison.Ordinal) &&
                cached.Vectors is not null)
                return cached.Vectors;

            var vectors = OpenReservoirFile(kind: artifact.Kind, artifactId: artifact.ArtifactId,
                bgeModelId: artifact.BgeModelId, dimension: artifact.EmbeddingDimension);
            if (cached is not null && string.Equals(cached.ArtifactId, artifact.ArtifactId, StringComparison.Ordinal))
                cached.Vectors = vectors;
            return vectors;
        }
    }

    private GraderEpochArtifact? ReadAcceptedLocked(string kind)
    {
        if (kind is not (GraderEpochKinds.Code or GraderEpochKinds.NonCode)) return null;

        var directory = KindDirectory(kind);
        var pointerPath = Path.Combine(directory, "accepted.json");
        if (!File.Exists(pointerPath))
        {
            _cache.Remove(kind);
            return null;
        }

        try
        {
            var pointer = JsonSerializer.Deserialize<AcceptedPointer>(File.ReadAllBytes(pointerPath), Json);
            if (pointer is not { ArtifactId: { } artifactId } || !IsArtifactId(artifactId))
            {
                _logger.LogWarning(
                    message: "Grader epoch accepted pointer for kind {Kind} is unreadable; treating it as no artifact.",
                    kind);
                return null;
            }

            if (_cache.TryGetValue(kind, out var cached) &&
                string.Equals(cached.ArtifactId, artifactId, StringComparison.Ordinal))
                return cached.Artifact;

            var artifactPath = Path.Combine(directory, artifactId + ".json");
            if (!File.Exists(artifactPath)) return null;

            var artifact = JsonSerializer.Deserialize<GraderEpochArtifact>(File.ReadAllBytes(artifactPath), Json);
            if (artifact is null || !string.Equals(artifact.ArtifactId, artifactId, StringComparison.Ordinal))
                return null;

            _cache[kind] = new CacheEntry { ArtifactId = artifact.ArtifactId, Artifact = artifact };
            return artifact;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception: ex,
                message: "Grader epoch accepted artifact for kind {Kind} could not be read.",
                kind);
            return null;
        }
    }

    private void ClearAcceptedLocked(string kind)
    {
        var path = Path.Combine(KindDirectory(kind), "accepted.json");
        if (File.Exists(path)) File.Delete(path);
        _cache.Remove(kind);
    }

    private void DeleteTempsLocked(string kind)
    {
        var directory = KindDirectory(kind);
        if (!Directory.Exists(directory)) return;
        foreach (var temp in Directory.EnumerateFiles(directory, "*.tmp"))
            File.Delete(temp);
    }

    private string PrepareKind(string kind)
    {
        if (kind is not (GraderEpochKinds.Code or GraderEpochKinds.NonCode))
            throw new ArgumentOutOfRangeException(nameof(kind));

        var directory = KindDirectory(kind);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private string KindDirectory(string kind) =>
        Path.Combine(_storage.CurrentValue.ResolveGraderEpochDirectory(), kind);

    private float[][] OpenReservoirFile(string kind, string artifactId, string bgeModelId, int dimension)
    {
        var path = Path.Combine(KindDirectory(kind), artifactId + ".reservoir");
        if (!File.Exists(path)) return [];

        var records = GraderEpochReservoir.Read(File.ReadAllBytes(path));
        var key = _keys.GetOrCreate();
        var opened = new List<float[]>(records.Count);
        foreach (var (correlationId, blob) in records)
        {
            if (!ResponseVectorSeal.TryOpenReservoir(key: key, artifactId: artifactId, correlationId: correlationId,
                    modelId: bgeModelId, blob: blob, vector: out var vector))
            {
                _logger.LogWarning(
                    message:
                    "Grader epoch reservoir entry failed authentication and was dropped. Artifact {ArtifactId}, correlation {CorrelationId}.",
                    artifactId,
                    correlationId);
                continue;
            }

            if (GraderEpochVectors.IsUsable(vector, dimension)) opened.Add(vector);
        }

        return opened.ToArray();
    }

    private static void WritePair(string directory, GraderEpochArtifact artifact, byte[] reservoir, bool failed)
    {
        var jsonTemp = Path.Combine(directory, artifact.ArtifactId + ".json.tmp");
        var reservoirTemp = Path.Combine(directory, artifact.ArtifactId + ".reservoir.tmp");
        File.WriteAllBytes(jsonTemp, JsonSerializer.SerializeToUtf8Bytes(artifact, Json));
        File.WriteAllBytes(reservoirTemp, reservoir);

        var destination = failed ? Path.Combine(directory, "failed") : directory;
        if (failed) Directory.CreateDirectory(destination);
        File.Move(jsonTemp, Path.Combine(destination, artifact.ArtifactId + ".json"), overwrite: true);
        File.Move(reservoirTemp, Path.Combine(destination, artifact.ArtifactId + ".reservoir"), overwrite: true);
    }

    private static void ReplaceAtomically(string directory, string fileName, byte[] bytes)
    {
        var temp = Path.Combine(directory, fileName + ".tmp");
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, Path.Combine(directory, fileName), overwrite: true);
    }

    private static void PruneFailed(string directory, int retention)
    {
        var failed = Path.Combine(directory, "failed");
        if (!Directory.Exists(failed)) return;

        var candidates = Directory.EnumerateFiles(failed, "*.json")
            .Select(path =>
            {
                var id = Path.GetFileNameWithoutExtension(path);
                return (Id: id, Created: ReadCreated(path), Path: path);
            })
            .OrderBy(item => item.Created)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();

        var keep = Math.Max(0, retention);
        for (var i = 0; i < candidates.Count - keep; i++)
        {
            File.Delete(candidates[i].Path);
            var reservoir = Path.Combine(failed, candidates[i].Id + ".reservoir");
            if (File.Exists(reservoir)) File.Delete(reservoir);
        }
    }

    private static DateTimeOffset ReadCreated(string path)
    {
        try
        {
            var artifact = JsonSerializer.Deserialize<GraderEpochArtifact>(File.ReadAllBytes(path), Json);
            return artifact?.CreatedAtUtc ?? DateTimeOffset.MinValue;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string? Mismatch(GraderEpochArtifact artifact, string teacherModel, string promptVersion,
        string bgeModelId, int featureSchemaVersion)
    {
        if (!string.Equals(artifact.TeacherModel, teacherModel, StringComparison.Ordinal)) return "teacher model";
        if (!string.Equals(artifact.TeacherPromptVersion, promptVersion, StringComparison.Ordinal))
            return "prompt version";
        if (!string.Equals(artifact.BgeModelId, bgeModelId, StringComparison.Ordinal)) return "BGE model id";
        if (artifact.FeatureSchemaVersion != featureSchemaVersion) return "feature schema";
        return null;
    }

    private static bool IsArtifactId([NotNullWhen(true)] string? id) =>
        id is { Length: 32 } && id.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Marker(string? value) => value ?? "null";

    private sealed class AcceptedPointer
    {
        public string? ArtifactId { get; init; }
    }

    private sealed class CacheEntry
    {
        internal required string ArtifactId { get; init; }

        internal required GraderEpochArtifact Artifact { get; init; }

        internal float[][]? Vectors { get; set; }
    }
}
