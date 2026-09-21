using TotallyHot.ArcRouter.Router.TextGeneration;

[assembly: AssemblyFixture(typeof(TotallyHot.ArcRouter.Tests.OnnxGenAiShutdownFixture))]

namespace TotallyHot.ArcRouter.Tests;

/// <summary>
/// Performs ONNX Runtime GenAI's process-wide shutdown once every test in this assembly has finished -
/// this test process's equivalent of the call the router makes at the end of <c>Program.Main</c>.
/// </summary>
/// <remarks>
/// Without it this test executable intermittently hung after printing its results (roughly one full run in
/// ten): the tests that exercise <see cref="OnnxTextGenerationClient"/> initialize GenAI, and a process that
/// exits without shutting it down can deadlock in <c>onnxruntime_genai.dll</c>'s own teardown. See
/// <see cref="OnnxGenAiShutdown"/>'s remarks for the captured stack. An assembly fixture is the one place in
/// a test run that is guaranteed to come after every test, which the shutdown needs: it is irreversible, so
/// running it any earlier would break every later test that touches GenAI.
/// </remarks>
public sealed class OnnxGenAiShutdownFixture : IDisposable
{
    /// <summary>Shuts GenAI down if any test in this run used it; does nothing, and loads nothing, otherwise.</summary>
    public void Dispose()
    {
        OnnxGenAiShutdown.Process.ShutdownIfUsed();
    }
}
