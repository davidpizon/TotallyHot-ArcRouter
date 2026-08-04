using System.Reflection;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Proxy.Management;
using TotallyHot.ArcRouter.Tests.Proxy;
using Moq;

namespace TotallyHot.ArcRouter.Tests.Proxy.Management;

/// <summary>
/// Guards <c>docs/router/backlog.md</c> item 1: <see cref="ManagementFacade"/>'s two provider write paths
/// used to rebuild <see cref="ProviderOptions"/> from a hand-written property list, and both lists had
/// fallen behind the type - silently clearing <see cref="ProviderOptions.EnableToolCallGuard"/> on every
/// edit and every Stop/Play toggle, and all four <c>Aws*</c> fields on an edit. The loss was durable, since
/// <see cref="IProviderConfigStore"/> persists the whole config on each mutation.
///
/// <para>
/// The reflection test below is the point of this file. Per-field assertions would just be a third
/// hand-maintained list with the same failure mode - it would pass happily the day someone adds a property
/// and forgets it.
/// </para>
///
/// <para>
/// That self-maintaining property takes <em>two</em> parts, and only one of them is the reflection.
/// <see cref="AssertAllPropertiesPreservedExcept"/> compares every property automatically, but a dropped
/// property is only <em>detectable</em> if the original held a non-default value - otherwise the loss
/// compares equal to the default and passes silently. <see cref="FullyPopulated"/> is hand-written, so it
/// is the half that can fall behind the type, exactly like the facade lists this file exists to guard.
/// <see cref="FullyPopulated_SetsEveryPropertyToANonDefaultValue"/> closes that gap: it fails the moment a
/// property is added to <see cref="ProviderOptions"/> and not populated here, so the pair together really
/// does cover a new property the moment it exists.
/// </para>
/// </summary>
public sealed class ProviderOptionsPreservationTests
{
    /// <summary>Every field set to a non-default value, so "was it carried?" is detectable for all of them.</summary>
    private static ProviderOptions FullyPopulated() => new()
    {
        Name = "Example Provider",
        BaseUrl = "https://example.invalid",
        ApiKey = "literal-key",
        ApiKeyEnvVar = "KEY_ENV",
        AuthHeaderName = "x-api-key",
        AuthHeaderScheme = "Token",
        Headers = [new ProviderHeader { Name = "anthropic-version", Value = "2023-06-01" }],
        IsFree = true,
        Enabled = false,
        EnableToolCallGuard = true,
        AwsRegion = "us-east-1",
        AwsAccessKeyIdEnvVar = "AWS_ID",
        AwsSecretAccessKeyEnvVar = "AWS_SECRET",
        AwsSessionTokenEnvVar = "AWS_TOKEN",
    };

    private static ManagementFacade CreateFacade(IProviderConfigStore store) =>
        new(store, Mock.Of<IEnvironmentVariableProvider>(), new HttpClient());

    private static InMemoryProviderConfigStore StoreWith(ProviderOptions provider) =>
        new(new ModelRoutingOptions
        {
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["bedrock"] = provider,
            },
        });

    // ----- The guard on the guard -----

    [Fact]
    public void FullyPopulated_SetsEveryPropertyToANonDefaultValue()
    {
        // Without this, the reflection assertion below is only as complete as this hand-written fixture. A
        // property added to ProviderOptions but not set here would sit at its default in `original`, so a
        // write path that dropped it would produce default == default and pass - the precise failure mode
        // (a hand-maintained list falling behind the type) that this file exists to prevent.
        var populated = FullyPopulated();
        var untouched = new ProviderOptions();

        var properties = typeof(ProviderOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);

        var stillDefault = properties
            .Where(property =>
            {
                var populatedValue = property.GetValue(populated);
                var defaultValue = property.GetValue(untouched);

                // Headers defaults to an empty list, so "non-default" means "has entries" rather than a
                // reference comparison, which would be trivially satisfied by any new list.
                if (populatedValue is IReadOnlyList<ProviderHeader> headers)
                {
                    return headers.Count == 0;
                }

                return Equals(populatedValue, defaultValue);
            })
            .Select(property => property.Name)
            .ToList();

        Assert.True(
            stillDefault.Count == 0,
            $"FullyPopulated() leaves these at their default: {string.Join(", ", stillDefault)}. "
                + "Set each to a distinct non-default value, otherwise a write path that drops the property "
                + "would still pass AssertAllPropertiesPreservedExcept.");
    }

    // ----- The guard that survives a future property being added -----

    [Fact]
    public async Task UpsertProvider_CarriesEveryPropertyItWasNotAskedToChange()
    {
        var original = FullyPopulated();
        var store = StoreWith(original);
        var facade = CreateFacade(store);

        // A minimal edit: change only the base URL. Everything else must survive untouched.
        await facade.UpsertProviderAsync("bedrock", new ProviderWriteRequest(
            BaseUrl: "https://changed.invalid",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null),
            TestContext.Current.CancellationToken);

        var updated = store.Snapshot.Options.Providers["bedrock"];

        Assert.Equal("https://changed.invalid", updated.BaseUrl);
        AssertAllPropertiesPreservedExcept(original, updated, nameof(ProviderOptions.BaseUrl));
    }

    [Fact]
    public async Task SetEnabled_CarriesEveryPropertyExceptEnabled()
    {
        var original = FullyPopulated();
        var store = StoreWith(original);
        var facade = CreateFacade(store);

        await facade.SetEnabledAsync("bedrock", new ProviderEnabledWriteRequest(Enabled: true), TestContext.Current.CancellationToken);

        var updated = store.Snapshot.Options.Providers["bedrock"];

        Assert.True(updated.Enabled);
        AssertAllPropertiesPreservedExcept(original, updated, nameof(ProviderOptions.Enabled));
    }

    /// <summary>
    /// Reflects over every public property of <see cref="ProviderOptions"/> and asserts each one either
    /// matches the original or is a name the caller expected to change. Adding a property to
    /// <see cref="ProviderOptions"/> automatically brings it under this assertion.
    /// </summary>
    private static void AssertAllPropertiesPreservedExcept(
        ProviderOptions original,
        ProviderOptions updated,
        params string[] expectedToChange)
    {
        var properties = typeof(ProviderOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // If this ever reads zero, the assertions below would vacuously pass and the guard would be dead.
        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            if (expectedToChange.Contains(property.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var before = property.GetValue(original);
            var after = property.GetValue(updated);

            // Headers is a list; compare by content so a rebuilt-but-equivalent list still counts as
            // preserved, which is what the caller actually cares about.
            if (before is IReadOnlyList<ProviderHeader> beforeHeaders && after is IReadOnlyList<ProviderHeader> afterHeaders)
            {
                Assert.Equal(
                    beforeHeaders.Select(h => (h.Name, h.Value, h.ValueEnvVar)),
                    afterHeaders.Select(h => (h.Name, h.Value, h.ValueEnvVar)));
                continue;
            }

            Assert.True(
                Equals(before, after),
                $"ProviderOptions.{property.Name} was not preserved: expected '{before}', found '{after}'. " +
                "If a property was just added to ProviderOptions, make sure the facade's write paths carry it.");
        }
    }

    // ----- The two specific fields the old hand-written lists dropped -----

    [Fact]
    public async Task UpsertProvider_PreservesEnableToolCallGuard()
    {
        // The regression that motivated this file: editing any provider through the GUI silently disarmed
        // its tool-call guard.
        var store = StoreWith(FullyPopulated());
        var facade = CreateFacade(store);

        await facade.UpsertProviderAsync("bedrock", new ProviderWriteRequest(
            "https://changed.invalid", null, null, null, null),
            TestContext.Current.CancellationToken);

        Assert.True(store.Snapshot.Options.Providers["bedrock"].EnableToolCallGuard);
    }

    [Fact]
    public async Task UpsertProvider_PreservesEveryAwsField()
    {
        // A Bedrock provider edited through the GUI used to lose its region and credential env-var names,
        // silently falling back to the SDK's ambient credential chain - or failing outright.
        var store = StoreWith(FullyPopulated());
        var facade = CreateFacade(store);

        await facade.UpsertProviderAsync("bedrock", new ProviderWriteRequest(
            "https://changed.invalid", null, null, null, null),
            TestContext.Current.CancellationToken);

        var updated = store.Snapshot.Options.Providers["bedrock"];
        Assert.Equal("us-east-1", updated.AwsRegion);
        Assert.Equal("AWS_ID", updated.AwsAccessKeyIdEnvVar);
        Assert.Equal("AWS_SECRET", updated.AwsSecretAccessKeyEnvVar);
        Assert.Equal("AWS_TOKEN", updated.AwsSessionTokenEnvVar);
    }

    [Fact]
    public async Task SetEnabled_PreservesEnableToolCallGuard()
    {
        // The Stop/Play toggle's own regression: WithEnabled copied every property by hand and had fallen
        // one behind, so toggling a provider off and on again permanently disarmed its guard.
        var store = StoreWith(FullyPopulated());
        var facade = CreateFacade(store);

        await facade.SetEnabledAsync("bedrock", new ProviderEnabledWriteRequest(Enabled: true), TestContext.Current.CancellationToken);

        Assert.True(store.Snapshot.Options.Providers["bedrock"].EnableToolCallGuard);
    }

    // ----- Adding a brand-new provider still gets the documented defaults -----

    [Fact]
    public async Task UpsertProvider_ForANewProvider_AppliesTheDocumentedDefaults()
    {
        // MergeProvider now starts from `new ProviderOptions()` when there is no existing provider, so its
        // own initializers supply the terminal fallbacks the old hand-written `?? "Authorization"` chain did.
        // This pins that they still agree.
        var store = new InMemoryProviderConfigStore(new ModelRoutingOptions());
        var facade = CreateFacade(store);

        await facade.UpsertProviderAsync("fresh", new ProviderWriteRequest(
            BaseUrl: "https://fresh.invalid",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null),
            TestContext.Current.CancellationToken);

        var created = store.Snapshot.Options.Providers["fresh"];
        Assert.Equal("https://fresh.invalid", created.BaseUrl);
        Assert.Equal("Authorization", created.AuthHeaderName);
        Assert.Equal("Bearer", created.AuthHeaderScheme);
        Assert.True(created.Enabled);
        Assert.False(created.IsFree);
        Assert.False(created.EnableToolCallGuard);
        Assert.Empty(created.Headers);
    }

    // ----- Three-way name merge semantics: null/empty/value -----

    [Fact]
    public async Task UpsertProvider_WithNullProviderName_PreservesExistingName()
    {
        var store = StoreWith(FullyPopulated());
        var facade = CreateFacade(store);

        await facade.UpsertProviderAsync("bedrock", new ProviderWriteRequest(
            BaseUrl: "https://changed.invalid",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null,
            ProviderName: null),
            TestContext.Current.CancellationToken);

        var updated = store.Snapshot.Options.Providers["bedrock"];
        Assert.Equal("Example Provider", updated.Name);
    }

    [Fact]
    public async Task UpsertProvider_WithEmptyProviderName_ClearsNameToNull()
    {
        var store = StoreWith(FullyPopulated());
        var facade = CreateFacade(store);

        await facade.UpsertProviderAsync("bedrock", new ProviderWriteRequest(
            BaseUrl: "https://changed.invalid",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null,
            ProviderName: ""),
            TestContext.Current.CancellationToken);

        var updated = store.Snapshot.Options.Providers["bedrock"];
        Assert.Null(updated.Name);
    }

    [Fact]
    public async Task UpsertProvider_WithWhitespaceProviderName_ClearsNameToNull()
    {
        var store = StoreWith(FullyPopulated());
        var facade = CreateFacade(store);

        await facade.UpsertProviderAsync("bedrock", new ProviderWriteRequest(
            BaseUrl: "https://changed.invalid",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null,
            ProviderName: "  \t\n  "),
            TestContext.Current.CancellationToken);

        var updated = store.Snapshot.Options.Providers["bedrock"];
        Assert.Null(updated.Name);
    }

    [Fact]
    public async Task UpsertProvider_WithValidProviderName_SetsName()
    {
        var store = StoreWith(FullyPopulated());
        var facade = CreateFacade(store);

        await facade.UpsertProviderAsync("bedrock", new ProviderWriteRequest(
            BaseUrl: "https://changed.invalid",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null,
            ProviderName: "New Provider Name"),
            TestContext.Current.CancellationToken);

        var updated = store.Snapshot.Options.Providers["bedrock"];
        Assert.Equal("New Provider Name", updated.Name);
    }

    [Fact]
    public async Task UpsertProvider_WithPaddedProviderName_TrimsWhitespace()
    {
        var store = StoreWith(FullyPopulated());
        var facade = CreateFacade(store);

        await facade.UpsertProviderAsync("bedrock", new ProviderWriteRequest(
            BaseUrl: "https://changed.invalid",
            AuthHeaderName: null,
            AuthHeaderScheme: null,
            ApiKey: null,
            ApiKeyEnvVar: null,
            ProviderName: "  New Provider Name  "),
            TestContext.Current.CancellationToken);

        var updated = store.Snapshot.Options.Providers["bedrock"];
        Assert.Equal("New Provider Name", updated.Name);
    }
}

