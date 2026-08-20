namespace TotallyHot.ArcRouter.Router.Orchestrator;

/// <summary>
/// Names each cluster by its top TF-IDF-distinguishing terms (docs/router/self-organizing-classification-plan.md
/// Phase T2e): a class-based TF-IDF over <see cref="LogRegTextTokenizer"/>'s shared tokenization rule -
/// a term's score for a cluster is its in-cluster frequency weighted by the inverse of how many documents
/// (across every cluster) contain it, so a term common to every cluster (e.g. "the", "please") scores low
/// everywhere while a term concentrated in one cluster scores high there. Only meaningful when transcript
/// capture is enabled, since prompt text is the input; a cluster with no documents gets an empty term list.
/// </summary>
public static class ClusterTermExtractor
{
    /// <summary>
    /// Computes the top <paramref name="topTermsPerCluster"/> distinguishing terms for each cluster.
    /// </summary>
    /// <param name="clusterDocuments">One list of prompt texts per cluster, in cluster-index order.</param>
    /// <param name="topTermsPerCluster">The maximum number of terms to return per cluster.</param>
    /// <returns>One list of terms per cluster, in cluster-index order, ranked highest-scoring first.</returns>
    public static IReadOnlyList<IReadOnlyList<string>> ExtractTopTerms(
        IReadOnlyList<IReadOnlyList<string>> clusterDocuments, int topTermsPerCluster = 5)
    {
        ArgumentNullException.ThrowIfNull(clusterDocuments);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(topTermsPerCluster, 0);

        var totalDocumentCount = clusterDocuments.Sum(docs => docs.Count);
        if (totalDocumentCount == 0)
        {
            return clusterDocuments.Select(_ => (IReadOnlyList<string>)Array.Empty<string>()).ToList();
        }

        // Document frequency across every cluster, needed for the inverse-document-frequency weight below.
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        var perClusterTermFrequency = new List<Dictionary<string, int>>(clusterDocuments.Count);

        foreach (var documents in clusterDocuments)
        {
            var termFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var document in documents)
            {
                var seenInThisDocument = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in LogRegTextTokenizer.Tokenize(document))
                {
                    termFrequency[token] = termFrequency.GetValueOrDefault(token) + 1;
                    if (seenInThisDocument.Add(token))
                    {
                        documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
                    }
                }
            }

            perClusterTermFrequency.Add(termFrequency);
        }

        var result = new List<IReadOnlyList<string>>(clusterDocuments.Count);
        foreach (var termFrequency in perClusterTermFrequency)
        {
            var scored = termFrequency
                .Select(kvp => (Term: kvp.Key, Score: kvp.Value * Math.Log((double)totalDocumentCount / (1 + documentFrequency[kvp.Key]))))
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Term, StringComparer.Ordinal)
                .Take(topTermsPerCluster)
                .Select(pair => pair.Term)
                .ToList();

            result.Add(scored);
        }

        return result;
    }
}
