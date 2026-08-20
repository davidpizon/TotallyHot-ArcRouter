using TotallyHot.ArcRouter.Router.Orchestrator;

namespace TotallyHot.ArcRouter.Tests.Router.Orchestrator;

/// <summary>
/// Covers <see cref="ClusterTermExtractor.ExtractTopTerms"/> - the class-based TF-IDF term extraction
/// behind a cluster's human-readable name (docs/router/self-organizing-classification-plan.md Phase T2e).
/// </summary>
public class ClusterTermExtractorTests
{
    [Fact]
    public void ExtractTopTerms_TermConcentratedInOneCluster_RanksHighInThatClusterOnly()
    {
        // "sql" repeats heavily across every cluster-0 document (high term frequency, moderate document
        // frequency) while each filler word appears exactly once anywhere (low term frequency, but also
        // low document frequency) - enough repetition that sql's term-frequency weight outweighs the
        // rarity bonus a one-off filler word would otherwise win on.
        IReadOnlyList<IReadOnlyList<string>> documents =
        [
            ["sql sql sql fix the bug", "sql sql sql schema failed again", "sql sql sql migration issue arose"],
            ["write a proof of correctness", "prove the theorem holds", "induction step verified today"],
        ];

        var terms = ClusterTermExtractor.ExtractTopTerms(documents, topTermsPerCluster: 1);

        Assert.Equal(["sql"], terms[0]);
        Assert.DoesNotContain("sql", terms[1]);
    }

    [Fact]
    public void ExtractTopTerms_TermCommonToEveryCluster_ScoresLowEverywhere()
    {
        IReadOnlyList<IReadOnlyList<string>> documents =
        [
            ["please fix the sql migration"],
            ["please prove the theorem"],
        ];

        var terms = ClusterTermExtractor.ExtractTopTerms(documents, topTermsPerCluster: 1);

        // "please" appears in every cluster's single document, so its IDF weight collapses toward zero -
        // the distinguishing term ("sql"/"migration" vs "prove"/"theorem") should win the single slot.
        Assert.DoesNotContain("please", terms[0]);
        Assert.DoesNotContain("please", terms[1]);
    }

    [Fact]
    public void ExtractTopTerms_EmptyClusterDocuments_ReturnsEmptyTermListsForEveryCluster()
    {
        IReadOnlyList<IReadOnlyList<string>> documents = [[], []];

        var terms = ClusterTermExtractor.ExtractTopTerms(documents);

        Assert.Equal(2, terms.Count);
        Assert.Empty(terms[0]);
        Assert.Empty(terms[1]);
    }

    [Fact]
    public void ExtractTopTerms_NoDocumentsAtAll_ReturnsEmptyTermListsRatherThanThrowing()
    {
        IReadOnlyList<IReadOnlyList<string>> documents = [[]];

        var terms = ClusterTermExtractor.ExtractTopTerms(documents);

        Assert.Single(terms);
        Assert.Empty(terms[0]);
    }
}
