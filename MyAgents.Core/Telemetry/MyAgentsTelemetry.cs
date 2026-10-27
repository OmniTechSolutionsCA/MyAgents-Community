using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MyAgents.Core;

/// <summary>
/// Shared OpenTelemetry primitives for the MyAgents framework.
/// Core middleware and higher-level features use these names so spans and
/// metrics remain consistent across runtimes and packages.
/// </summary>
public static class MyAgentsTelemetry
{
    /// <summary>
    /// The activity source name emitted by the framework.
    /// </summary>
    public const string ActivitySourceName = "MyAgents";

    /// <summary>
    /// The meter name emitted by the framework.
    /// </summary>
    public const string MeterName = "MyAgents";

    /// <summary>
    /// Framework version attached to telemetry primitives.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// Shared activity source for turn- and tool-level spans.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    /// <summary>
    /// Shared meter for framework counters and histograms.
    /// </summary>
    public static readonly Meter Meter = new(MeterName, Version);
}