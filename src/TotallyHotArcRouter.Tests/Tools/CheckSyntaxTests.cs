using Microsoft.CodeAnalysis;
using TotallyHot.ArcRouter.Tools;

namespace TotallyHot.ArcRouter.Tests.Tools;

/// <summary>
/// Covers syntax-checking behavior.
/// </summary>
public class CheckSyntaxTests
{
    [Fact]
    public void Check_WithValidCode_ReturnsNoErrors()
    {
        var checkSyntax = new CheckSyntax();
        var code = @"
public class MyClass
{
    public void MyMethod()
    {
    }
}";

        var diagnostics = checkSyntax.Check(code);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Check_WithInvalidCode_ReturnsErrors()
    {
        var checkSyntax = new CheckSyntax();
        var code = @"
public class MyClass
{
    public void MyMethod()
    {
        // Missing closing brace
    ";

        var diagnostics = checkSyntax.Check(code);

        Assert.NotEmpty(diagnostics);
        Assert.All(collection: diagnostics,
            action: d => Assert.Equal(expected: DiagnosticSeverity.Error, actual: d.Severity));
    }

    [Fact]
    public void Check_WithWhitespaceOnlyCode_ReturnsNoErrors()
    {
        var checkSyntax = new CheckSyntax();

        var diagnostics = checkSyntax.Check("   \r\n\t ");

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Check_WithNullCode_ThrowsArgumentNullException()
    {
        var checkSyntax = new CheckSyntax();

        Assert.Throws<ArgumentNullException>(() => checkSyntax.Check(null!));
    }
}