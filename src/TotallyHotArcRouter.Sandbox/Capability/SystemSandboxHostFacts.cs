using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TotallyHot.ArcRouter.Sandbox.Capability;

/// <summary>Reads real host facts from the operating system.</summary>
/// <remarks>
/// Every check here tests for the capability itself rather than inferring it from an OS version: the Linux
/// properties look for the actual <c>/dev/kvm</c> node and cgroup mount, and
/// <see cref="IsJobObjectAvailable"/> creates a real Job Object and closes it. Version inference would
/// report capabilities a locked-down or containerized host does not actually grant.
/// </remarks>
public sealed class SystemSandboxHostFacts : ISandboxHostFacts
{
    // Lazy, not a plain property call: creating a Job Object is a kernel round-trip, and while the probe
    // reads this exactly once at startup, the property is public and must not become a syscall per read.
    // The OperatingSystem.IsWindows() guard lives inside the factory rather than at the call site because
    // that is the form the platform-compatibility analyzer (CA1416) recognizes as guarding the
    // windows-only call below.
    private static readonly Lazy<bool> JobObjectProbe = new(
        () => OperatingSystem.IsWindows() && ProbeJobObjectSupport(),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <inheritdoc />
    public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <inheritdoc />
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <inheritdoc />
    public bool IsKvmAvailable => IsLinux && File.Exists("/dev/kvm");

    /// <inheritdoc />
    public bool IsCgroupV2Available => IsLinux && File.Exists("/sys/fs/cgroup/cgroup.controllers");

    /// <inheritdoc />
    public bool IsJobObjectAvailable => JobObjectProbe.Value;

    /// <summary>
    /// Creates an anonymous Job Object and immediately closes it, answering "can this host actually do
    /// this?" rather than "does this Windows version usually support it?".
    /// </summary>
    /// <remarks>
    /// Failure is reported, never thrown. This runs during startup capability probing, where an exotic or
    /// stripped host must degrade to Tier 0 rather than prevent the process from starting - the same
    /// posture the Linux checks take by returning false for a missing file.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static bool ProbeJobObjectSupport()
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
            return handle != IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A host without a usable kernel32 export for this is simply a host without the capability.
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    // DllImport rather than the newer source-generated LibraryImport: LibraryImport requires
    // AllowUnsafeBlocks project-wide (SYSLIB1062), and relaxing that across the sandbox project - whose
    // entire purpose is containment - is a poor trade for two detection calls that marshal nothing but
    // pointer-sized integers. Both parameters below are typed as IntPtr precisely so there is no string or
    // struct marshalling to get wrong.

    /// <summary>Creates a Job Object; returns <see cref="IntPtr.Zero"/> on failure.</summary>
    /// <param name="lpJobAttributes">Security attributes; always <see cref="IntPtr.Zero"/> here (default descriptor, non-inheritable).</param>
    /// <param name="lpName">The job's name; always <see cref="IntPtr.Zero"/> here, so the probe creates an unnamed job and cannot collide with another process's named one.</param>
    [DllImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

    /// <summary>Closes an open object handle.</summary>
    /// <param name="hObject">The handle to close.</param>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static extern bool CloseHandle(IntPtr hObject);
}
