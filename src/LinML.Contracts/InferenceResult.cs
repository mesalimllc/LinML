namespace LinML.Contracts;

/// <summary>
/// Result of an inference run: outputs, which provider actually ran, and diagnostics.
/// </summary>
public sealed record InferenceResult
{
    /// <summary>The provider that was requested, or null for auto (best available, no --provider).</summary>
    public required ProviderId? RequestedProvider { get; init; }

    /// <summary>The provider that actually executed.</summary>
    public required ProviderId SelectedProvider { get; init; }

    /// <summary>Whether fallback occurred: an explicit request that did not run. Auto selection never falls back.</summary>
    public bool FallbackOccurred => RequestedProvider is not null && RequestedProvider != SelectedProvider;

    /// <summary>If fallback occurred, the chain of providers tried and why.</summary>
    public IReadOnlyList<FallbackStep> FallbackChain { get; init; } = [];

    /// <summary>Output tensors.</summary>
    public required IReadOnlyList<TensorOutput> Outputs { get; init; }

    /// <summary>Total inference duration.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Inference duration in milliseconds.</summary>
    public double LatencyMs => Duration.TotalMilliseconds;

    /// <summary>Model identity that was executed.</summary>
    public required ModelIdentity Model { get; init; }

    /// <summary>Runtime version used.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>Diagnostics from execution.</summary>
    public IReadOnlyList<DiagnosticEntry> Diagnostics { get; init; } = [];

    /// <summary>Per-node execution placement from ORT profiling.</summary>
    public IReadOnlyList<NodePlacement> NodePlacements { get; init; } = [];
}

public sealed record FallbackStep
{
    public required ProviderId Provider { get; init; }
    public required string Reason { get; init; }
}

/// <summary>A named tensor output.</summary>
public sealed record TensorOutput
{
    public required string Name { get; init; }
    public required TensorDataType DataType { get; init; }
    public required IReadOnlyList<int> Shape { get; init; }
    public required IReadOnlyList<float> Data { get; init; }
}
