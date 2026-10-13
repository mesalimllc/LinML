using LinML.Contracts;
using Microsoft.Extensions.Logging;

namespace LinML.Policy;

/// <summary>
/// Selects a provider from ordered candidates based on discovery and readiness.
/// Implements the selection policy: try candidates in order, respect RequireFirst.
/// </summary>
public sealed class ProviderSelector
{
    private readonly IProviderDiscovery _discovery;
    private readonly IProviderReadinessProbe _readinessProbe;
    private readonly ILogger<ProviderSelector> _logger;

    public ProviderSelector(
        IProviderDiscovery discovery,
        IProviderReadinessProbe readinessProbe,
        ILogger<ProviderSelector> logger)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _readinessProbe = readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Select a provider from the request's ordered candidates.
    /// </summary>
    public SelectionDecision Select(SelectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Candidates.Count == 0)
        {
            return new SelectionDecision
            {
                Success = false,
                FailureReason = "No candidate providers specified."
            };
        }

        var discovery = _discovery.Discover();
        var availableProviders = discovery.AvailableProviders.ToHashSet();

        var rejected = new List<RejectedCandidate>();

        foreach (var candidate in request.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if provider is even visible in the runtime
            if (!availableProviders.Contains(candidate))
            {
                var reason = request.UnavailableReasons.TryGetValue(candidate, out var known)
                    ? known
                    : $"Provider '{candidate}' is not available in the runtime.";
                _logger.LogWarning("Provider {Provider} rejected: {Reason}", candidate, reason);
                rejected.Add(new RejectedCandidate { Provider = candidate, Reason = reason });

                if (request.RequireFirst && rejected.Count == 1)
                {
                    return new SelectionDecision
                    {
                        Success = false,
                        RejectedCandidates = rejected,
                        FailureReason = reason
                    };
                }
                continue;
            }

            // Probe readiness
            var readiness = _readinessProbe.Probe(candidate, cancellationToken);

            if (!readiness.IsReady)
            {
                var reason = $"Provider '{candidate}' failed readiness at phase {readiness.FailedPhase}: {readiness.FailureReason}";
                _logger.LogWarning("Provider {Provider} rejected: {Reason}", candidate, reason);
                rejected.Add(new RejectedCandidate { Provider = candidate, Reason = reason });

                if (request.RequireFirst && rejected.Count == 1)
                {
                    return new SelectionDecision
                    {
                        Success = false,
                        RejectedCandidates = rejected,
                        FailureReason = reason
                    };
                }
                continue;
            }

            // Success: this provider is ready
            _logger.LogInformation("Selected provider {Provider}", candidate);
            return new SelectionDecision
            {
                Success = true,
                SelectedProvider = candidate,
                RejectedCandidates = rejected
            };
        }

        // All candidates rejected
        var allRejected = string.Join("; ", rejected.Select(r => $"{r.Provider}: {r.Reason}"));
        return new SelectionDecision
        {
            Success = false,
            RejectedCandidates = rejected,
            FailureReason = $"All candidate providers rejected. {allRejected}"
        };
    }
}
