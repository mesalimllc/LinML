namespace LinML.Contracts;

/// <summary>
/// The result of provider selection: which provider was chosen, or why selection failed.
/// </summary>
public sealed record SelectionDecision
{
    /// <summary>True if a provider was successfully selected.</summary>
    public required bool Success { get; init; }

    /// <summary>The selected provider (when Success is true).</summary>
    public ProviderId? SelectedProvider { get; init; }

    /// <summary>Ordered list of providers that were tried and rejected.</summary>
    public IReadOnlyList<RejectedCandidate> RejectedCandidates { get; init; } = [];

    /// <summary>Failure reason when Success is false.</summary>
    public string? FailureReason { get; init; }

    /// <summary>True if the selection involved fallback from the first candidate.</summary>
    public bool FallbackUsed => Success && RejectedCandidates.Count > 0;
}

public sealed record RejectedCandidate
{
    public required ProviderId Provider { get; init; }
    public required string Reason { get; init; }
}
