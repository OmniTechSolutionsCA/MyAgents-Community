using System.Text.Json;
using MyAgents.Core.Tools;

namespace MyAgents.Agents.Scrapai;

public abstract class ScrapaiToolBase : ITool
{
    private readonly IProcessRunner _runner;
    private readonly string _executablePath;

    protected ScrapaiToolBase(IProcessRunner runner, string executablePath)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _executablePath = executablePath;
    }

    public abstract string Name { get; }

    public abstract string? Description { get; }

    public abstract ValueTask<string> InvokeAsync(string jsonArguments, CancellationToken cancellationToken = default);

    protected async ValueTask<string> InvokeProcessAsync(IEnumerable<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(_executablePath, args, cancellationToken);
            return SerializeResponse(
                exitCode: result.ExitCode,
                stdout: result.Stdout,
                stderr: result.Stderr,
                error: result.ExitCode == 0 ? null : $"Process exited with code {result.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SerializeResponse(exitCode: -1, stdout: string.Empty, stderr: string.Empty, error: $"Execution failed: {ex.Message}");
        }
    }

    protected static bool TryDeserialize<T>(string json, out T? value, out string response)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json);
            response = string.Empty;
            return true;
        }
        catch (Exception)
        {
            value = default;
            response = SerializeResponse(exitCode: -1, stdout: string.Empty, stderr: string.Empty, error: "Invalid JSON arguments");
            return false;
        }
    }

    protected static string SerializeValidationError(string message)
        => SerializeResponse(exitCode: -1, stdout: string.Empty, stderr: string.Empty, error: message);

    protected static string SerializeResponse(int exitCode, string stdout, string stderr, string? error)
    {
        return JsonSerializer.Serialize(new
        {
            exitCode,
            stdout,
            stderr,
            error,
        });
    }
}