namespace TotallyHot.ArcRouter.Sandbox.Tier1;

/// <summary>
/// The interpreter invocation and namespace flags for a jailed run. Pure/OS-agnostic so it is unit-testable
/// off Linux; the actual launch is performed by <see cref="IJailLauncher"/>.
/// </summary>
/// <param name="Interpreter">The interpreter executable (e.g. <c>python3</c>, <c>node</c>, <c>sh</c>).</param>
/// <param name="ScriptFileName">The file name (within the working directory) the snippet is written to.</param>
/// <param name="UnshareFlags">The <c>unshare(1)</c> flags requesting the isolating namespaces.</param>
public sealed record JailCommand(string Interpreter, string ScriptFileName, IReadOnlyList<string> UnshareFlags);

/// <summary>
/// Builds the interpreter command and namespace flags for a Tier-1 jailed run. Network is isolated by an
/// empty network namespace (no veth), so there is no route out.
/// </summary>
public static class JailCommandBuilder
{
    /// <summary>
    /// The namespace flags shared by every jailed run: a fresh network namespace (air-gap), plus PID,
    /// mount, UTS, and IPC namespaces, forking so the interpreter becomes PID 1 in its own namespace.
    /// </summary>
    public static IReadOnlyList<string> DefaultUnshareFlags { get; } =
    [
        "--net",
        "--pid",
        "--mount",
        "--uts",
        "--ipc",
        "--fork",
        "--kill-child",
        "--map-root-user",
    ];

    /// <summary>Builds the jail command for a request's language.</summary>
    /// <param name="language">The snippet language.</param>
    /// <returns>The interpreter command and namespace flags.</returns>
    /// <exception cref="NotSupportedException">Thrown for a language with no Tier-1 runtime.</exception>
    public static JailCommand Build(SandboxLanguage language)
    {
        return language switch
        {
            SandboxLanguage.Python => new JailCommand("python3", "snippet.py", DefaultUnshareFlags),
            SandboxLanguage.JavaScript => new JailCommand("node", "snippet.js", DefaultUnshareFlags),
            SandboxLanguage.Shell => new JailCommand("sh", "snippet.sh", DefaultUnshareFlags),
            _ => throw new NotSupportedException($"No Tier-1 runtime for language '{language}'."),
        };
    }

    /// <summary>Indicates whether a language has a Tier-1 interpreter.</summary>
    /// <param name="language">The language to test.</param>
    /// <returns><see langword="true"/> when a Tier-1 interpreter exists.</returns>
    public static bool IsSupported(SandboxLanguage language) =>
        language is SandboxLanguage.Python or SandboxLanguage.JavaScript or SandboxLanguage.Shell;
}

