using System.Diagnostics.Metrics;

namespace MyAgents.Core.Security;

/// <summary>
/// Telemetry for egress defense, emitted on the shared framework meter so it surfaces alongside
/// existing MyAgents counters.
/// </summary>
internal static class CanaryTelemetry
{
    public const string ViolationInstrument = "myagents.canary.violations";

    public static readonly Counter<long> Violations = MyAgentsTelemetry.Meter.CreateCounter<long>(
        ViolationInstrument,
        "{violations}",
        "Number of egress-defense violations detected, by channel, severity and detector.");
}
