using TotallyHot.ArcRouter.Judge;
using TotallyHot.ArcRouter.Proxy.Management;

namespace TotallyHot.ArcRouter.Tests.Judge;

/// <summary>Covers <see cref="QuestionHmacHasher"/> normalization and keyed hashing.</summary>
public class QuestionHmacHasherTests
{
    [Fact]
    public void TryCompute_NormalizesWhitespaceAndIsStable()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "secrets.dat");
        var hasher = new QuestionHmacHasher(new ProtectedSecretStore(path));

        var first = hasher.TryCompute("Fix   the\n bug");
        var second = hasher.TryCompute("Fix the bug");

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void TryCompute_MissingQuestion_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "secrets.dat");
        var hasher = new QuestionHmacHasher(new ProtectedSecretStore(path));

        Assert.Null(hasher.TryCompute("   "));
    }
}
