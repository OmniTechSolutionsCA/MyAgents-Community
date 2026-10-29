using System.Diagnostics;

namespace MyAgents.Core.Tools
{
    /// <summary>
    /// Default <see cref="IProcessRunner"/> implementation that uses <see cref="Process"/>.
    /// </summary>
    public sealed class DefaultProcessRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            ArgumentNullException.ThrowIfNull(args);

            // Honour a cancellation that arrived before the child even started. Without this the
            // process would be launched, killed by the registration below, and reported as a
            // normal result — silently ignoring the caller's token.
            cancellationToken.ThrowIfCancellationRequested();

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            if (!process.Start())
                throw new InvalidOperationException($"Failed to start process {fileName}");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.Token.Register(static state =>
            {
                var target = (Process)state!;
                try { target.Kill(entireProcessTree: true); } catch { }
            }, process);

            // The reads must observe the token as well: killing the child does not necessarily
            // close its redirected pipes, so an un-cancellable read can keep the runner alive
            // long after the caller asked to stop.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
    }
}
