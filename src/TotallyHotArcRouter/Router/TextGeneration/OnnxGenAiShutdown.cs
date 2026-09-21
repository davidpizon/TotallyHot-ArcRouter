using Microsoft.ML.OnnxRuntimeGenAI;

namespace TotallyHot.ArcRouter.Router.TextGeneration;

/// <summary>
/// Performs ONNX Runtime GenAI's process-wide shutdown - <c>OgaShutdown</c>, reached through
/// <see cref="OgaHandle.Dispose()"/> - exactly once, and only in a process that actually used GenAI.
/// </summary>
/// <remarks>
/// <para>
/// GenAI requires this call before process exit, while its native worker threads are still alive to be
/// stopped. Skipping it leaves the teardown to <c>onnxruntime_genai.dll</c>'s own static destructors, which
/// run during <c>DLL_PROCESS_DETACH</c> - after <c>ExitProcess</c> has already terminated every other
/// thread. One of those destructors waits on a condition variable that only a worker thread can signal, so
/// whenever a worker was mid-flight at exit the wait can never complete and the process hangs with a single
/// thread in <c>_Cnd_wait</c>. That is not a theory: it was captured with <c>cdb</c> on a hung
/// <c>TotallyHotArcRouter.Tests</c> process (<c>ntdll!LdrShutdownProcess</c> →
/// <c>ucrtbase!execute_onexit_table</c> → <c>onnxruntime_genai</c> → <c>MSVCP140!_Cnd_wait</c>). The race
/// makes it intermittent, which is why it survived so long; the router service runs the same exit path.
/// </para>
/// <para>
/// Why "only if used": <c>OgaShutdown</c> is a P/Invoke, so calling it unconditionally would load
/// <c>onnxruntime-genai.dll</c> - and ONNX Runtime with it - into a process that never touched GenAI, solely
/// to shut it down. <see cref="MarkUsed"/> is called immediately before every native GenAI entry point
/// (both <c>new Model(...)</c> sites in <see cref="OnnxTextGenerationClient"/>), and before rather than after
/// because a load that throws has still initialized GenAI's native state.
/// </para>
/// <para>
/// Why process level, not <see cref="OnnxTextGenerationClient.DisposeAsync"/>: the shutdown is global and
/// irreversible. A client disposal is not a process exit - tests construct and dispose many clients - and
/// once shut down, GenAI cannot be used again in that process. Call <see cref="ShutdownIfUsed"/> only after
/// every GenAI object is disposed: the router does it in <c>Program.Main</c>'s <c>finally</c>, after the host
/// and its container are gone.
/// </para>
/// </remarks>
internal sealed class OnnxGenAiShutdown
{
    private readonly Action _shutdown;
    private int _used;

    /// <summary>Initializes a new instance with the action that performs the native shutdown.</summary>
    /// <param name="shutdown">Performs the shutdown. Injected so tests can observe it without calling the real, irreversible one.</param>
    internal OnnxGenAiShutdown(Action shutdown)
    {
        ArgumentNullException.ThrowIfNull(shutdown);
        _shutdown = shutdown;
    }

    /// <summary>The instance tracking this process's real GenAI usage, wired to the real <c>OgaShutdown</c>.</summary>
    internal static OnnxGenAiShutdown Process { get; } = new(static () => new OgaHandle().Dispose());

    /// <summary>
    /// Records that GenAI's native library is about to be (or has been) initialized in this process, so
    /// <see cref="ShutdownIfUsed"/> knows a shutdown is owed. Idempotent and thread-safe.
    /// </summary>
    internal void MarkUsed()
    {
        Volatile.Write(ref _used, 1);
    }

    /// <summary>
    /// Runs the shutdown if <see cref="MarkUsed"/> was called since the last shutdown; otherwise does nothing,
    /// and in particular does not load the native library. Safe to call more than once and from more than one
    /// thread: at most one caller performs the shutdown.
    /// </summary>
    /// <returns><see langword="true"/> if this call performed the shutdown.</returns>
    internal bool ShutdownIfUsed()
    {
        if (Interlocked.Exchange(ref _used, 0) == 0) return false;

        _shutdown();
        return true;
    }
}
