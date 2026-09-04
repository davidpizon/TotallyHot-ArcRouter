namespace TotallyHot.ArcRouter.Tests.Integration;

/// <summary>
/// Serializes integration tests that may share system resources.
/// </summary>
[CollectionDefinition("Integration", DisableParallelization = true)]
public sealed class IntegrationTestCollection;