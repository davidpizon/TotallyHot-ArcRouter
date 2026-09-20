using TotallyHot.ArcRouter.Quality.Parsing;

namespace TotallyHot.ArcRouter.Quality.Tests;

/// <summary>
/// Covers the shell-specific structural scanner: the cases the shared delimiter-balance heuristic used
/// to fail on valid scripts (here-documents, <c>${#var}</c>, <c>case</c> arms) and the imbalances it
/// must still reject.
/// </summary>
public class ShellStructuralParserTests
{
    private readonly StructuralParser _parser = new();

    // ---- Previously weak: valid shell the bracket-count heuristic rejected ----

    [Fact]
    public void Check_HereDocumentWithUnmatchedPunctuationInBody_IsValid()
    {
        AssertAccepted("""
            cat <<EOF
            This has unmatched ) and ] and }
            EOF
            """);
    }

    [Fact]
    public void Check_QuotedHereDocument_IsValid()
    {
        AssertAccepted("""
            cat <<'EOF'
            $(not expanded) and a stray )
            EOF
            """);
    }

    [Fact]
    public void Check_EscapedHereDocumentDelimiter_IsValid()
    {
        AssertAccepted("""
            cat <<\EOF
            $(not expanded) and a stray )
            EOF
            """);
    }

    [Fact]
    public void Check_DashHereDocumentStripsLeadingTabs_IsValid()
    {
        AssertAccepted("cat <<-EOF\n\tbody with )\n\tEOF\n");
    }

    [Fact]
    public void Check_HereDocumentInsideCommandSubstitution_IsValid()
    {
        AssertAccepted("""
            var=$(cat <<EOF
            hello )
            EOF
            )
            """);
    }

    [Fact]
    public void Check_MultipleHereDocumentsOnOneLine_AreValid()
    {
        AssertAccepted("""
            cat <<EOF1 <<EOF2
            line1 )
            EOF1
            line2 ]
            EOF2
            """);
    }

    [Theory]
    [InlineData("echo ${#var}\n")]
    [InlineData("echo ${#args[@]}\n")]
    [InlineData("name=${file#/tmp/}\n")]
    [InlineData("name=${file##*/}\n")]
    [InlineData("name=${file%.txt}\n")]
    [InlineData("echo $#\n")]
    [InlineData("echo ${var:-$(foo)}\n")]
    public void Check_ParameterExpansionWithHash_IsValid(string code)
    {
        AssertAccepted(code);
    }

    [Fact]
    public void Check_CaseStatement_IsValid()
    {
        AssertAccepted("""
            case $x in
              foo) echo foo;;
              bar) echo bar;;
              *) echo other;;
            esac
            """);
    }

    [Fact]
    public void Check_CaseStatementWithOptionalParens_IsValid()
    {
        AssertAccepted("""
            case $x in
              (foo) echo foo;;
              bar|baz) echo bar;;
            esac
            """);
    }

    [Fact]
    public void Check_CaseInsideCommandSubstitution_IsValid()
    {
        AssertAccepted("echo $(case $x in foo) echo bar;; esac)\n");
    }

    [Theory]
    [InlineData("echo 'unmatched ) here'\n")]
    [InlineData("echo \"unmatched ( in a string\"\n")]
    [InlineData("echo 'it'\"'\"'s (fine)'\n")]
    [InlineData("echo $'it\\'s (fine)'\n")]
    [InlineData("echo `ls (inside backticks)`\n")]
    [InlineData("echo \"result: $(foo (bar))\"\n")]
    [InlineData("echo \"${#var}\"\n")]
    public void Check_QuotedUnmatchedPunctuation_IsValid(string code)
    {
        AssertAccepted(code);
    }

    [Fact]
    public void Check_CommentWithUnmatchedParen_IsValid()
    {
        AssertAccepted("# Note: this (is a comment\necho hello\n");
    }

    [Fact]
    public void Check_HashInTheMiddleOfAWord_IsNotAComment()
    {
        AssertAccepted("echo foo#bar(ok)\n");
    }

    [Fact]
    public void Check_TypicalFunction_IsValidButNotAuthoritative()
    {
        var verdict = _parser.Check(code: "foo() { echo hi; }\n", language: CodeLanguage.Shell);

        Assert.True(verdict.IsValid);
        Assert.False(verdict.IsAuthoritative);
        Assert.Empty(verdict.Errors);
    }

    [Fact]
    public void Check_TestBracketCommand_IsValid()
    {
        AssertAccepted("if [ -f \"$file\" ]; then echo ok; fi\n");
    }

    // ---- Still invalid: real imbalances the scanner must not start swallowing ----

    [Fact]
    public void Check_AssignmentNamedCase_DoesNotSwallowUnbalancedParen()
    {
        var verdict = _parser.Check(code: "case=1\necho 1)\n", language: CodeLanguage.Shell);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unbalanced ')'.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnbalancedCloseParen_IsInvalid()
    {
        var verdict = _parser.Check(code: "echo 1)\n", language: CodeLanguage.Shell);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unbalanced ')'.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnbalancedOpenParen_IsInvalid()
    {
        var verdict = _parser.Check(code: "echo (1\n", language: CodeLanguage.Shell);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unbalanced '('.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnterminatedSingleQuotedString_IsInvalid()
    {
        var verdict = _parser.Check(code: "echo 'abc", language: CodeLanguage.Shell);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unterminated string literal.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnterminatedHereDocument_IsInvalid()
    {
        var verdict = _parser.Check(code: "cat <<EOF\nhello\n", language: CodeLanguage.Shell);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unterminated here-document.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnterminatedCommandSubstitution_IsInvalid()
    {
        var verdict = _parser.Check(code: "echo $(foo\n", language: CodeLanguage.Shell);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unbalanced '('.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_UnterminatedParamExpansion_IsInvalid()
    {
        var verdict = _parser.Check(code: "echo ${var\n", language: CodeLanguage.Shell);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Unbalanced '{'.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_EmptySnippet_IsInvalid()
    {
        var verdict = _parser.Check(code: "   \n\t", language: CodeLanguage.Shell);

        Assert.False(verdict.IsValid);
        Assert.Contains(expected: "Snippet is empty.", collection: verdict.Errors);
    }

    [Fact]
    public void Check_NullCode_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ShellStructuralParser.Check(code: null!));
    }

    /// <summary>
    /// Pins the gap the new scanner closed: the generic delimiter walk still rejects a here-document
    /// whose body contains a stray close-paren, which is valid shell and must not fail the quality grade.
    /// </summary>
    [Fact]
    public void DelimiterBalance_StillRejectsTheHereDocumentCase()
    {
        const string code = "cat <<EOF\nThis has unmatched )\nEOF\n";

        Assert.False(DelimiterBalance.IsBalanced(code: code, error: out _));
        AssertAccepted(code);
    }

    /// <summary>
    /// Pins the <c>${#var}</c> gap: a <c>#</c> after <c>{</c> is a length operator, not a comment, so
    /// the closing brace is still part of the script.
    /// </summary>
    [Fact]
    public void DelimiterBalance_StillRejectsTheLengthExpansionCase()
    {
        const string code = "echo ${#var}\n";

        Assert.False(DelimiterBalance.IsBalanced(code: code, error: out _));
        AssertAccepted(code);
    }

    /// <summary>
    /// Pins the <c>case</c> gap: a pattern terminator <c>)</c> has no matching opener.
    /// </summary>
    [Fact]
    public void DelimiterBalance_StillRejectsTheCaseArmCase()
    {
        const string code = "case $x in foo) echo foo;; esac\n";

        Assert.False(DelimiterBalance.IsBalanced(code: code, error: out _));
        AssertAccepted(code);
    }

    private void AssertAccepted(string code)
    {
        var verdict = _parser.Check(code: code, language: CodeLanguage.Shell);

        Assert.True(verdict.IsValid, userMessage: string.Join("; ", values: verdict.Errors));
        Assert.False(verdict.IsAuthoritative);
        Assert.Empty(verdict.Errors);
    }
}
