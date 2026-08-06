using TotallyHot.ArcRouter.Hosting;
using TotallyHot.ArcRouter.Models;
using TotallyHot.ArcRouter.Proxy;
using TotallyHot.ArcRouter.Router;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using Xunit;

namespace TotallyHot.ArcRouter.Tests
{
    public class ProgramTests
    {
        [Fact]
        public void CreateHostBuilder_BuildsSuccessfully()
        {
            // Arrange
            var args = new string[] { };

            // Act
            var host = Program.CreateHostBuilder(args).Build();

            // Assert
            Assert.NotNull(host);
        }

        // PLAN.md "Local Proxy CLI" pillar: `TotallyHot.ArcRouter --model <name>` should register the forced
        // model name for RequestInterceptor to pick up (see RequestInterceptorTests' single-model-serving
        // coverage) - this test confirms Program.cs's CLI parsing actually reaches DI, black-box, via the
        // same public CreateHostBuilder API the other tests in this file already use.
        [Fact]
        public void CreateHostBuilder_WithModelFlag_RegistersForcedModelName()
        {
            var host = Program.CreateHostBuilder(["--model", "gpt-5.4"]).Build();

            var options = host.Services.GetRequiredService<SingleModelServingOptions>();

            Assert.Equal("gpt-5.4", options.ForcedModelName);
        }

        [Fact]
        public void CreateHostBuilder_WithoutModelFlag_RegistersNullForcedModelName()
        {
            var host = Program.CreateHostBuilder([]).Build();

            var options = host.Services.GetRequiredService<SingleModelServingOptions>();

            Assert.Null(options.ForcedModelName);
        }

        // The --model=value form (not just the two-token --model value form) must be recognized too -
        // Host.CreateDefaultBuilder's command-line provider understands both, so ExtractModelArg must too.
        [Fact]
        public void CreateHostBuilder_WithModelEqualsFlag_RegistersForcedModelName()
        {
            var host = Program.CreateHostBuilder(["--model=gpt-5.4"]).Build();

            var options = host.Services.GetRequiredService<SingleModelServingOptions>();

            Assert.Equal("gpt-5.4", options.ForcedModelName);
        }

        // Regression test: Host.CreateDefaultBuilder adds a command-line configuration provider that
        // binds unrecognized "--key value" or "--key=value" pairs into IConfiguration under "key". If
        // --model weren't stripped before being passed through, it would leak in as a "model"
        // configuration key that nothing else expects - assert it doesn't, in both forms.
        [Fact]
        public void CreateHostBuilder_WithModelFlag_DoesNotLeakIntoConfiguration()
        {
            var host = Program.CreateHostBuilder(["--model", "gpt-5.4"]).Build();

            var configuration = host.Services.GetRequiredService<IConfiguration>();

            Assert.Null(configuration["model"]);
        }

        [Fact]
        public void CreateHostBuilder_WithModelEqualsFlag_DoesNotLeakIntoConfiguration()
        {
            var host = Program.CreateHostBuilder(["--model=gpt-5.4"]).Build();

            var configuration = host.Services.GetRequiredService<IConfiguration>();

            Assert.Null(configuration["model"]);
        }

        // --model with no value must fail loudly at startup, not silently pass a bogus/empty value
        // through to Host.CreateDefaultBuilder's command-line configuration provider.
        [Fact]
        public void CreateHostBuilder_WithModelFlagAndNoValue_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => Program.CreateHostBuilder(["--model"]));
        }

        [Fact]
        public void CreateHostBuilder_WithModelEqualsFlagAndEmptyValue_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => Program.CreateHostBuilder(["--model="]));
        }

        // End-to-end regression: RequestInterceptor's construction-time validation actually fires when
        // wired through the real host, not just when constructed directly in RequestInterceptorTests.
        // A minimal, explicit in-memory ModelRouting configuration is layered on top of whatever
        // Host.CreateDefaultBuilder's default appsettings.json discovery does or doesn't find, so this
        // test's outcome depends only on RequestInterceptor's own validation logic, not on the test
        // run's working/content-root directory happening to contain the real app's appsettings.json.
        [Fact]
        public void CreateHostBuilder_WithUnconfiguredModelFlag_ThrowsWhenRequestInterceptorIsResolved()
        {
            var host = BuildHostWithMinimalModelRouting(["--model", "not-a-real-model"]);

            Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<RequestInterceptor>());
        }

        [Fact]
        public void CreateHostBuilder_WithConfiguredModelFlag_ResolvesRequestInterceptorSuccessfully()
        {
            var host = BuildHostWithMinimalModelRouting(["--model", "gpt-5.4"]);

            Assert.NotNull(host.Services.GetRequiredService<RequestInterceptor>());
        }

        private static IHost BuildHostWithMinimalModelRouting(string[] args) =>
            Program.CreateHostBuilder(args)
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ModelRouting:Providers:openai:BaseUrl"] = "https://api.openai.com",
                    ["ModelRouting:Providers:openai:AuthHeaderName"] = "Authorization",
                    ["ModelRouting:ModelList:0:ModelName"] = "gpt-5.4",
                    ["ModelRouting:ModelList:0:Provider"] = "openai",
                    ["ModelRouting:ModelList:0:ProviderModelId"] = "gpt-5.4-test",
                }))
                .Build();

        // Regression test: Host.CreateDefaultBuilder enables ServiceProviderOptions.ValidateOnBuild (and
        // ValidateScopes) only in the Development environment, so a missing/unresolvable dependency like
        // IRouterModelClient is silently tolerated when CreateHostBuilder_BuildsSuccessfully runs in the
        // default (non-Development) test environment, but throws eagerly at Build() time in Development.
        // This reproduces Program.cs's ConfigureServices registrations directly under that stricter mode,
        // without depending on ambient environment variables during test execution.
        [Fact]
        public void ConfigureServices_ProducesResolvableServiceGraph_UnderValidateOnBuild()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<RoutingOptions>(_ => { });
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

            services.AddTotallyHotArcRouter();
            services.AddSingleton<IRouterModelClient, NotImplementedRouterModelClient>();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            Assert.NotNull(provider.GetRequiredService<AgentAsARouter>());
        }
    }
}

