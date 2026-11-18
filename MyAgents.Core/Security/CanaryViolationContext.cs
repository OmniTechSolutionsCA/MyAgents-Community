namespace MyAgents.Core.Security;

/// <summary>
/// Describes a violation handed to an <see cref="ICanaryViolationHandler"/>.
/// </summary>
/// <param name="Channel">Which outbound payload tripped the detector.</param>
/// <param name="Result">The scan result, including every finding.</param>
/// <param name="SessionId">The session the violation occurred in.</param>
public sealed record CanaryViolationContext(
    EgressChannel Channel,
    EgressScanResult Result,
    string SessionId);
