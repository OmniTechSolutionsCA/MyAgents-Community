using System.Threading;
using System.Threading.Tasks;

namespace MyAgents.Core.Tools
{
    /// <summary>
    /// Abstraction for running a child process and capturing its output.
    /// </summary>
    public interface IProcessRunner
    {
        /// <summary>
        /// Runs the specified executable with the provided arguments.
        /// </summary>
        /// <param name="fileName">The executable name or full path.</param>
        /// <param name="args">Argument list (no shell expansion).</param>
        /// <param name="cancellationToken">Token used to cancel the operation. When cancelled the process will be killed.</param>
        /// <returns>A <see cref="ProcessResult"/> containing exit code, stdout, and stderr.</returns>
        Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken = default);
    }
}