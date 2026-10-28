using System.Diagnostics;
using MyAgents.Core.Tools;
using NUnit.Framework;

namespace MyAgents.Community.Tests;

/// <summary>
/// Contract tests for <see cref="DefaultProcessRunner"/>.
/// </summary>
/// <remarks>
/// The runner had no tests at all. These pin the parts of its contract the CA2016 findings
/// touch: output capture, exit-code propagation, and — most importantly — that cancellation
/// is honoured rather than left to however long a child process decides to live.
/// </remarks>
public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Test]
    public async Task RunAsync_captures_stdout_and_exit_code()
    {
        var (fileName, args) = Echo("hello-from-child");

        var result = await new DefaultProcessRunner().RunAsync(fileName, args);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(result.Stdout.Trim(), Is.EqualTo("hello-from-child"));
            Assert.That(result.Stderr, Is.Empty);
        });
    }

    [Test]
    public async Task RunAsync_propagates_non_zero_exit_code()
    {
        var (fileName, args) = ExitWith(3);

        var result = await new DefaultProcessRunner().RunAsync(fileName, args);

        Assert.That(result.ExitCode, Is.EqualTo(3));
    }

    [Test]
    public async Task RunAsync_throws_OperationCanceledException_when_cancelled_before_start()
    {
        var (fileName, args) = Sleep(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.ThrowsAsync(
            Is.InstanceOf<OperationCanceledException>(),
            async () => await new DefaultProcessRunner().RunAsync(fileName, args, cts.Token));
    }

    /// <summary>
    /// Cancelling a running child must abort the runner promptly instead of waiting for the
    /// child to finish on its own. Without forwarding the token to the output readers, a child
    /// that keeps its stdout handle open can hold the runner far past the cancellation.
    /// </summary>
    [Test]
    public async Task RunAsync_aborts_promptly_when_cancelled_while_running()
    {
        var (fileName, args) = Sleep(TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource();

        var stopwatch = Stopwatch.StartNew();
        var run = new DefaultProcessRunner().RunAsync(fileName, args, cts.Token);

        // Give the child a moment to actually start before cancelling.
        await Task.Delay(150, TestContext.CurrentContext.CancellationToken);
        await cts.CancelAsync();

        var assertion = Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await run);
        stopwatch.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(assertion, Is.Not.Null);
            Assert.That(stopwatch.Elapsed, Is.LessThan(TestTimeout),
                "cancellation must not wait for the child process to exit on its own");
        });
    }

    [Test]
    public void RunAsync_throws_when_executable_does_not_exist()
    {
        Assert.CatchAsync(
            async () => await new DefaultProcessRunner().RunAsync(
                "definitely-not-a-real-executable-9f3a2b", []),
            "a missing executable must surface as an exception, not a silent result");
    }

    [Test]
    public async Task RunAsync_reports_normal_result_when_child_exits_cleanly_without_cancellation()
    {
        var (fileName, args) = Echo("no-cancel");
        using var cts = new CancellationTokenSource();

        var result = await new DefaultProcessRunner().RunAsync(fileName, args, cts.Token);

        Assert.That(result.ExitCode, Is.EqualTo(0),
            "a live token must not turn a clean run into a cancellation");
    }

    [Test]
    public void RunAsync_rejects_null_arguments()
    {
        var runner = new DefaultProcessRunner();

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentNullException>(async () => await runner.RunAsync(null!, []));
            Assert.ThrowsAsync<ArgumentNullException>(async () => await runner.RunAsync("cmd", null!));
        });
    }

    // ── Platform-specific shell helpers ─────────────────────────────────────

    private static bool IsWindows => OperatingSystem.IsWindows();

    private static (string FileName, string[] Args) Echo(string text)
        => IsWindows ? ("cmd", ["/c", "echo", text]) : ("sh", ["-c", $"echo {text}"]);

    private static (string FileName, string[] Args) ExitWith(int code)
        => IsWindows ? ("cmd", ["/c", "exit", code.ToString()]) : ("sh", ["-c", $"exit {code}"]);

    private static (string FileName, string[] Args) Sleep(TimeSpan duration)
        => IsWindows
            ? ("powershell", ["-NoProfile", "-NonInteractive", "-Command", $"Start-Sleep -Seconds {(int)duration.TotalSeconds}"])
            : ("sh", ["-c", $"sleep {(int)duration.TotalSeconds}"]);
}
