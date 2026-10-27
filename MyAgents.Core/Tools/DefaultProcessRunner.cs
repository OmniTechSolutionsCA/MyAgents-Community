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


            try
            {
                if (!process.Start())
                    throw new InvalidOperationException($"Failed to start process {fileName}");


                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.Token.Register(() =>
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                });

                // read streams asynchronously
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(linkedCts.Token);

                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                var exitCode = process.ExitCode;

                return new ProcessResult(exitCode, stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                // propagate cancellation
                throw;
            }
        }
    }
}
