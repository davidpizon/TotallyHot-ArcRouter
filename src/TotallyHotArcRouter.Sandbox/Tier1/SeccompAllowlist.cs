namespace TotallyHot.ArcRouter.Sandbox.Tier1;

/// <summary>
/// The syscall allowlist applied to a Tier-1 jailed process. Everything outside this set is denied (the
/// process is killed with <c>SIGSYS</c>), which the executor maps to a seccomp denial and can escalate to
/// Tier 2. The set is the minimum a short interpreter run needs; it deliberately excludes socket/network,
/// mount, ptrace, and module syscalls.
/// </summary>
public static class SeccompAllowlist
{
    /// <summary>
    /// The permitted syscall names. Installed as a BPF filter by the native jailer; also used to document
    /// and test the policy surface independently of the kernel-specific numbering.
    /// </summary>
    public static IReadOnlyList<string> AllowedSyscalls { get; } =
    [
        // Process/thread lifecycle
        "exit", "exit_group", "clone", "clone3", "fork", "vfork", "execve", "execveat", "wait4", "waitid",
        "rt_sigaction", "rt_sigprocmask", "rt_sigreturn", "sigaltstack", "getpid", "gettid", "set_tid_address",
        "set_robust_list", "get_robust_list", "futex", "sched_yield", "prctl", "arch_prctl",
        // Memory
        "brk", "mmap", "mprotect", "munmap", "mremap", "madvise", "membarrier",
        // File I/O within the tmpfs work dir (no socket/network syscalls)
        "read", "write", "readv", "writev", "pread64", "pwrite64", "open", "openat", "openat2", "close",
        "close_range", "lseek", "stat", "fstat", "newfstatat", "lstat", "statx", "access", "faccessat",
        "faccessat2", "getdents64", "pipe", "pipe2", "dup", "dup2", "dup3", "fcntl", "ioctl", "poll", "ppoll",
        "pselect6", "epoll_create1", "epoll_ctl", "epoll_wait", "epoll_pwait", "eventfd2",
        // Time and identity (read-only)
        "clock_gettime", "clock_nanosleep", "nanosleep", "gettimeofday", "getrandom", "getuid", "geteuid",
        "getgid", "getegid", "getcwd", "uname", "sysinfo", "rseq",
    ];

    /// <summary>The default action for any syscall not in <see cref="AllowedSyscalls"/> (kill the thread).</summary>
    public const string DefaultAction = "SCMP_ACT_KILL_PROCESS";
}

