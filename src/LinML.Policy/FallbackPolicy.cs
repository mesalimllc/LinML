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
    /// Preference rank for the default fallback chain. Lower = tried first.
    /// GPU accelerators lead; CPU-ish EPs follow; CPU is always last.
    /// Providers not listed by the runtime are filtered out by the caller.
    /// </summary>
    private static int PreferenceRank(ProviderId provider) => provider.Value switch
    {
        "NvTensorRTRTXExecutionProvider" => 0, // TensorRT-RTX: best on RTX
        "TensorrtExecutionProvider" => 1,       // classic TensorRT
        "CUDAExecutionProvider" => 2,
        "MIGraphXExecutionProvider" => 3,       // AMD
        "QNNExecutionProvider" => 4,            // Snapdragon HTP
        "OpenVINOExecutionProvider" => 5,       // Intel CPU/GPU/NPU
        "CPUExecutionProvider" => 99,           // always last
        _ => 50                                 // unknown EPs: after known GPU, before CPU
    };

    /// <summary>
    /// Build an ordered candidate list from the providers the runtime actually
    /// lists: preferred provider first (if available), then remaining available
    /// providers in preference-rank order, CPU last.
    /// </summary>
    public IReadOnlyList<ProviderId> BuildCandidateList(
        InferenceRequest request,
        IReadOnlyCollection<ProviderId> availableProviders)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(availableProviders);

        var available = new HashSet<ProviderId>(availableProviders);
        var candidates = new List<ProviderId>();

        // Preferred provider first (even if not listed — selection will reject
        // it honestly and record why).
        if (request.PreferredProvider is not null && !candidates.Contains(request.PreferredProvider))
        {
            candidates.Add(request.PreferredProvider);
        }

        // Remaining available providers in preference-rank order.
        foreach (var provider in available
                     .Where(p => !candidates.Contains(p))
                     .OrderBy(PreferenceRank))
        {
            candidates.Add(provider);
        }

        // CPU is always the final fallback.
        if (!candidates.Contains(ProviderId.Cpu) && available.Contains(ProviderId.Cpu))
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
