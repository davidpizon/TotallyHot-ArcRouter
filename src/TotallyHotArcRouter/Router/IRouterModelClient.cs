namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Defines the contract for a client that can invoke a model.
/// </summary>
public interface IRouterModelClient
{
    /// <summary>
    /// Gets a response from the specified model for the given prompt.
    /// </summary>
    /// <param name="model">The model to invoke.</param>
    /// <param name="prompt">The prompt to send to the model.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The model's response.</returns>
    Task<string> GetResponseAsync(string model, string prompt, CancellationToken cancellationToken = default);
}

