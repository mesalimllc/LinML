namespace LinML.Contracts;

/// <summary>
/// A request to select a provider: ordered candidates, soft vs required, model context.
/// </summary>
public sealed record SelectionRequest
{
    /// <summary>Ordered candidate providers (first = most preferred).</summary>
    public required IReadOnlyList<ProviderId> Candidates { get; init; }

    /// <summary>If true, failure to use the first candidate is an error (no fallback).</summary>
    public bool RequireFirst { get; init; }

    /// <summary>Model context for per-model compatibility checks.</summary>
    public ModelIdentity? Model { get; init; }

    /// <summary>Additional options affecting selection.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>();
}
