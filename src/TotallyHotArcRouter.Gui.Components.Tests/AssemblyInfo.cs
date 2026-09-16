using Xunit.Sdk;
using Xunit.v3;

// Components rendered by these tests that inject a live LiveDataStore (Dashboard and every workspace
// under it - DashboardTests, CostAnalyticsTests, GovernanceTests, etc.) call LiveDataStore.StartAsync(),
// which attempts a real gRPC-Web connection to a deliberately unreachable test address and then retries
// on a 2-second background timer (LiveDataStore.ReconnectDelay) until the test's BunitContext is
// disposed. Under xUnit's default parallel test-collection execution, CPU contention across many
// concurrently-rendering bUnit tests can slow an individual test past that 2-second window, so the
// reconnect timer's background NotifyChanged/StateHasChanged fires mid-test and races a bUnit
// Find-then-Click sequence, changing event handler IDs between the two calls
// (Bunit.Rendering.UnknownEventHandlerIdException). This reproduced intermittently in CI (a shared,
// CPU-constrained runner) but not in local runs on a faster, less contended machine - serializing test
// execution removes the CPU contention that was the actual empirical trigger. A proper fix would give
// LiveDataStore's tests a fake telemetry client instead of a live (if doomed) network connection, which
// is a larger, separately-scoped change; this is the minimal fix for the CI flakiness itself.
[assembly: ParallelizationAttribute(Mode = ParallelMode.None)]
