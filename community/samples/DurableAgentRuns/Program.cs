using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyAgents.Core.Execution;
using MyAgents.Runtime.Workers;
using MyAgents.Runtime.Workers.Sqlite;

var databasePath = Path.GetFullPath(args.FirstOrDefault() ?? "agent-runs.db");
var builder = Host.CreateApplicationBuilder();
builder.Services.AddSingleton<IAgentRunBackend>(_ => new SqliteAgentRunBackend(databasePath));
builder.Services.AddSingleton<IAgentRunHandler, SampleRunHandler>();
builder.Services.AddMyAgentsRunWorker(options =>
{
    options.IdlePollInterval = TimeSpan.FromMilliseconds(250);
    options.LeaseDuration = TimeSpan.FromSeconds(5);
    options.LeaseRenewalInterval = TimeSpan.FromSeconds(1);
    options.BaseRetryDelay = TimeSpan.FromSeconds(1);
    options.MaximumRetryDelay = TimeSpan.FromSeconds(10);
});

using var host = builder.Build();
await host.StartAsync();

var backend = host.Services.GetRequiredService<IAgentRunBackend>();
var request = new AgentRunRequest(
    HandlerId: SampleRunHandler.StableHandlerId,
    InputJson: JsonSerializer.Serialize(new SampleInput("A durable run survived a process restart.")),
    IdempotencyKey: "durable-runs-sample-v1",
    ScopeKey: "sample");
var submission = await backend.SubmitAsync(request);
Console.WriteLine($"Run {submission.RunId} submitted to {databasePath} (deduplicated: {submission.WasDeduplicated}).");
Console.WriteLine("Interrupt this process while the handler is running, then restart it with the same database path to observe lease recovery.");

var stoppingToken = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
try
{
    while (!stoppingToken.IsCancellationRequested)
    {
        var record = await backend.GetAsync(submission.RunId);
        if (record is null)
            throw new InvalidOperationException("The submitted run could not be read back.");
        if (record.Status is AgentRunStatus.Succeeded or AgentRunStatus.Failed or AgentRunStatus.Cancelled or AgentRunStatus.DeadlineExceeded)
        {
            Console.WriteLine($"Run ended as {record.Status} after {record.Attempt} attempt(s). Output: {record.OutputJson}");
            break;
        }

        Console.WriteLine($"Run status: {record.Status}; attempt: {record.Attempt}");
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
    }
}
catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
{
    // Stop the host after Ctrl+C; the active run lease will expire and become recoverable.
}

await host.StopAsync();
