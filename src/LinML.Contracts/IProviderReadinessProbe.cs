namespace LinML.Contracts;

/// <summary>
/// Probes whether a provider can actually load, append to a session, and run.
/// </summary>
public interface IProviderReadinessProbe
{
    /// <summary>Probe readiness for a specific provider.</summary>
    /// <param name="provider">The provider to probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ProviderReadinessReport Probe(ProviderId provider, CancellationToken cancellationToken = default);
}
