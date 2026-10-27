using MyAgents.Core;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace MyAgents.Agents;

/// <summary>
/// Extension methods for registering MyAgents telemetry with OpenTelemetry.
/// Designed to work with both the standard OTel SDK and .NET Aspire's
/// automatic OTLP export pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Aspire usage (AppHost):</b> The .NET Aspire AppHost automatically configures
/// OTLP export — no exporter code is needed in your application. Just call the
/// extension methods below in your application's <c>Program.cs</c>:
/// </para>
/// <code>
/// // Program.cs (Aspire service project)
/// builder.Services.AddOpenTelemetry()
///     .AddMyAgentsTracing()
///     .AddMyAgentsMetrics();
/// </code>
///
/// <para>
/// <b>Non-Aspire usage:</b> Add your own exporter:
/// </para>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .AddMyAgentsTracing()
///     .AddMyAgentsMetrics()
///     .WithTracing(b => b.AddOtlpExporter())
///     .WithMetrics(b => b.AddOtlpExporter());
/// </code>
/// </remarks>
public static class MyAgentsOtelExtensions
{
    /// <summary>
    /// The OpenTelemetry activity source name emitted by the MyAgents framework.
    /// Use this when manually calling <c>AddSource()</c>.
    /// </summary>
    public const string ActivitySourceName = MyAgentsTelemetry.ActivitySourceName;

    /// <summary>
    /// The OpenTelemetry meter name emitted by the MyAgents framework.
    /// Use this when manually calling <c>AddMeter()</c>.
    /// </summary>
    public const string MeterName = MyAgentsTelemetry.MeterName;

    /// <summary>
    /// The activity source name emitted by MEAI's LLM call layer.
    /// </summary>
    public const string MeaiActivitySourceName = "Microsoft.Extensions.AI";

    /// <summary>
    /// Adds MyAgents distributed tracing instrumentation to the
    /// <see cref="TracerProviderBuilder"/>.
    /// Includes both MyAgents turn spans and Microsoft.Extensions.AI LLM call spans.
    /// </summary>
    /// <param name="builder">The <see cref="OpenTelemetryBuilder"/> to configure.</param>
    /// <returns>The same builder for chaining.</returns>
    public static OpenTelemetryBuilder AddMyAgentsTracing(this OpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithTracing(tracer =>
        {
            tracer
                .AddSource(ActivitySourceName)
                .AddSource(MeaiActivitySourceName);
        });
    }

    /// <summary>
    /// Adds MyAgents metrics instrumentation to the <see cref="MeterProviderBuilder"/>.
    /// Captures: <c>agent.turn.duration_ms</c>, <c>agent.turn.tokens</c>,
    /// <c>agent.turn.errors</c>.
    /// </summary>
    /// <param name="builder">The <see cref="OpenTelemetryBuilder"/> to configure.</param>
    /// <returns>The same builder for chaining.</returns>
    public static OpenTelemetryBuilder AddMyAgentsMetrics(this OpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithMetrics(metrics =>
        {
            metrics.AddMeter(MeterName);
        });
    }
}
