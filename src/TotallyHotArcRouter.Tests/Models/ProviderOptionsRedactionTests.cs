using System.Reflection;
using TotallyHot.ArcRouter.Models;

namespace TotallyHot.ArcRouter.Tests.Models;

/// <summary>
/// Guards the <c>ToString()</c> consequence of <see cref="ProviderOptions"/> becoming a <c>record</c>.
///
/// <para>
/// As a class its <c>ToString()</c> was the type name and carried nothing. The synthesized record printer
/// prints every property verbatim, so without the type's <c>PrintMembers</c> override
/// <see cref="ProviderOptions.ApiKey"/> would appear in plain text in any log line, exception message, or
/// debugger view that happened to include a provider. Nothing logs one today, which is exactly why this
/// needs a test rather than a comment - the leak would arrive with whichever future log statement first
/// interpolates a provider, far from the change that made it possible.
/// </para>
///
/// <para>
/// Only <see cref="EveryPublicProperty_IsAccountedForInPrintMembers"/> reflects over the type; it is what
/// makes this file self-maintaining, by failing when a property is added to
/// <see cref="ProviderOptions"/> and not handled in the hand-written <c>PrintMembers</c>. The two
/// value-level tests are ordinary assertions over <see cref="FullyPopulated"/> and cover only what that
/// fixture sets - they do not automatically extend to a new secret-carrying property. The reflection test
/// is what forces that property to be noticed, at which point choosing <c>Redacted(...)</c> for it is a
/// deliberate decision rather than an omission.
/// </para>
/// </summary>
public sealed class ProviderOptionsRedactionTests
{
    private const string Secret = "sk-live-must-never-be-printed";

    private static ProviderOptions FullyPopulated() => new()
    {
        BaseUrl = "https://api.example.invalid/v1",
        ProviderType = "Anthropic",
        ApiKey = Secret,
        ApiKeyEnvVar = "EXAMPLE_API_KEY",
        AuthHeaderName = "x-api-key",
        AuthHeaderScheme = string.Empty,
        Headers = [new ProviderHeader { Name = "anthropic-version", Value = Secret }],
        IsFree = true,
        Enabled = false,
        EnableToolCallGuard = true,
        AwsRegion = "us-east-1",
        AwsAccessKeyIdEnvVar = "AWS_ACCESS_KEY_ID",
        AwsSecretAccessKeyEnvVar = "AWS_SECRET_ACCESS_KEY",
        AwsSessionTokenEnvVar = "AWS_SESSION_TOKEN",
    };

    [Fact]
    public void ToString_NeverContainsASecretValue()
    {
        var rendered = FullyPopulated().ToString();

        Assert.DoesNotContain(Secret, rendered, StringComparison.Ordinal);
        Assert.Contains("<redacted>", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_StillShowsTheNonSecretFieldsWorthDiagnosingWith()
    {
        // Redaction that hides everything is its own failure: the operator loses the context that makes a
        // logged provider useful. The EnvVar names in particular name where a secret lives without carrying
        // one, and are the first thing to check when a credential problem is being diagnosed.
        var rendered = FullyPopulated().ToString();

        Assert.Contains("https://api.example.invalid/v1", rendered, StringComparison.Ordinal);
        Assert.Contains("EXAMPLE_API_KEY", rendered, StringComparison.Ordinal);
        Assert.Contains("AWS_SECRET_ACCESS_KEY", rendered, StringComparison.Ordinal);
        Assert.Contains("anthropic-version", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPublicProperty_IsAccountedForInPrintMembers()
    {
        // The self-maintaining half, and the reason this file reflects instead of asserting a fixed list.
        // PrintMembers is hand-written, so a property added later is silently omitted from ToString() - and
        // if that property is a secret, the omission is the safe direction, but if it is diagnostic context
        // the loss is invisible. Requiring every property name to appear forces the author to make a
        // deliberate choice about the new field rather than defaulting into either mistake.
        var rendered = FullyPopulated().ToString();

        var missing = typeof(ProviderOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => !rendered.Contains(name, StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"ProviderOptions.PrintMembers omits: {string.Join(", ", missing)}. Add each to PrintMembers - "
                + "redacted via Redacted(...) if it carries a secret, verbatim if it is diagnostic context.");
    }
}

