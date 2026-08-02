using System.Collections.Concurrent;

namespace TotallyHot.ArcRouter.Router;

/// <summary>
/// Defines the contract for a component that persists and retrieves router memory.
/// </summary>
public interface IRouterMemoryStore
{
    /// <summary>
    /// Loads the router memory from the persistent store.
    /// </summary>
    /// <returns>A concurrent dictionary representing the router's memory of model scores.</returns>
    Task<ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>>> LoadAsync();

    /// <summary>
    /// Saves the router memory to the persistent store.
    /// </summary>
    /// <param name="memory">A concurrent dictionary representing the router's memory.</param>
    Task SaveAsync(ConcurrentDictionary<string, ConcurrentDictionary<string, List<double>>> memory);
}

