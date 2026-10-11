namespace LinML.Contracts;

/// <summary>
/// Result of probing whether a provider can actually load, append to a session,
/// and run a smoke test. Distinct from discovery: discovery lists what's visible,
/// readiness proves what works.
/// </summary>
public sealed record ProviderReadinessReport
{
    public required ProviderId Provider { get; init; }

    /// <summary>Whether the provider passed all readiness phases.</summary>
    public required bool IsReady { get; init; }

    /// <summary>Which phase failed, if any.</summary>
    public ReadinessPhase? FailedPhase { get; init; }

    /// <summary>Human-readable failure reason when not ready.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Per-phase results.</summary>
    public IReadOnlyList<ReadinessPhaseResult> PhaseResults { get; init; } = [];

    /// <summary>Time taken for each phase.</summary>
    public IReadOnlyDictionary<ReadinessPhase, TimeSpan> PhaseDurations { get; init; } =
        new Dictionary<ReadinessPhase, TimeSpan>();
}

public enum ReadinessPhase
{
    /// <summary>Native library load (dlopen equivalent).</summary>
    Load,
    /// <summary>Append provider to session options.</summary>
    Append,
    /// <summary>Create session and run a minimal smoke inference.</summary>
    Smoke
}

public sealed record ReadinessPhaseResult
{
    public required ReadinessPhase Phase { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
}
