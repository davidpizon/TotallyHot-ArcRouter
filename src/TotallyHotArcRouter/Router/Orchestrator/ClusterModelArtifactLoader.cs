using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// Reads a trained <see cref="ClusterModelArtifact"/> from disk, tolerating the honest "no model yet"
/// state of a fresh install rather than throwing. Shared by the <c>cluster_best</c> voter (Phase T3) and
/// the taxonomy comparison job (Phase T4) so both agree on what "no usable cluster model" means - a
/// divergence there would let one of them score against an artifact the other refused to load.
/// </summary>
public static class ClusterModelArtifactLoader
{
    /// <summary>
    /// Loads and deserializes the artifact at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The resolved artifact path.</param>
    /// <param name="logger">Receives the absent/unreadable diagnostics.</param>
    /// <param name="consumer">A short name for the caller, used in the log lines so the two consumers stay distinguishable.</param>
    /// <returns>The artifact, or <see langword="null"/> when the file is absent or could not be read.</returns>
    /// <remarks>
    /// The artifact is per-installation, trained from the operator's own traffic, and never checked in, so
    /// its absence is expected rather than exceptional - both callers degrade to abstaining or skipping
    /// instead of shipping a fabricated stand-in.
    /// </remarks>
    public static ClusterModelArtifact? TryLoad(string path, ILogger logger, string consumer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        if (!File.Exists(path))
        {
            logger.LogDebug("No cluster model found at {Path} for {Consumer}; it will stand down until one is trained.", path, consumer);
            return null;
        }

        try
        {
            var model = ClusterModelArtifactSerializer.Deserialize(File.ReadAllText(path));
            logger.LogInformation(
                "Loaded cluster model from {Path} for {Consumer} (trained from: {TrainedFrom}).", path, consumer, model.TrainedFrom);
            return model;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            logger.LogWarning(ex, "Failed to load the cluster model from {Path} for {Consumer}.", path, consumer);
            return null;
        }
    }
}
