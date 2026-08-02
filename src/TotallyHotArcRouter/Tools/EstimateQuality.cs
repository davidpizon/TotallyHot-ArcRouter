namespace TotallyHot.ArcRouter.Tools
{
    /// <summary>
    /// A tool to estimate the quality of a code snippet.
    /// </summary>
    public class EstimateQuality
    {
        /// <summary>
        /// Estimates the quality of the provided code based on simple heuristics.
        /// </summary>
        /// <param name="code">The code to evaluate.</param>
        /// <returns>A quality score between 0.0 and 1.0.</returns>
        public double Estimate(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return 0.0;
            }

            // Simple heuristic: longer code is not necessarily better, but very short code is often incomplete.
            // This is a placeholder for a more sophisticated quality estimation model.
            var score = 1.0 - (1.0 / (1 + code.Length / 100.0));

            // Penalize for lack of comments
            if (!code.Contains("//"))
            {
                score *= 0.9;
            }

            return score;
        }
    }
}

