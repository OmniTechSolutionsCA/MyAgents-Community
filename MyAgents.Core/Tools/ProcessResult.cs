namespace MyAgents.Core.Tools
{
    /// <summary>
    /// Result returned by <see cref="IProcessRunner"/> after a process exits.
    /// </summary>
    public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}