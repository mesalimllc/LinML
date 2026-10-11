using LinML.Contracts;
using Microsoft.Extensions.Logging;

namespace LinML.Policy;

/// <summary>
/// Defines the fallback policy: when soft selection fails on the preferred provider,
/// which providers to try next and how to record the fallback.
/// </summary>
public sealed class FallbackPolicy
{
    private readonly ILogger<FallbackPolicy> _logger;

    public FallbackPolicy(ILogger<FallbackPolicy> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Build an ordered candidate list for an inference request.
    /// If a preferred provider is specified, it goes first; CPU is always the final fallback.
    /// </summary>
    public IReadOnlyList<ProviderId> BuildCandidateList(InferenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidates = new List<ProviderId>();

        // Preferred provider first
        if (request.PreferredProvider is not null)
        {
            candidates.Add(request.PreferredProvider);
        }

        // CPU as fallback (unless it's already the preferred)
        if (request.PreferredProvider != ProviderId.Cpu)
        {
            candidates.Add(ProviderId.Cpu);
        }

        return candidates;
    }

    /// <summary>
    /// Determine whether fallback is allowed for this request.
    /// </summary>
    public bool IsFallbackAllowed(InferenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return !request.RequireProvider;
    }
}
