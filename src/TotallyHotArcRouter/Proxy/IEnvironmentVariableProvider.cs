namespace TotallyHot.ArcRouter.Proxy;

/// <summary>
/// Abstracts environment variable access so credential resolution can be unit tested without mutating process state.
/// </summary>
public interface IEnvironmentVariableProvider
{
    /// <summary>
    /// Gets the value of the environment variable with the given name, or <see langword="null"/> if it is not set.
    /// </summary>
    /// <param name="name">The environment variable name.</param>
    string? GetVariable(string name);
}

/// <summary>
/// Reads environment variables from the current process.
/// </summary>
public sealed class EnvironmentVariableProvider : IEnvironmentVariableProvider
{
    /// <inheritdoc />
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}

