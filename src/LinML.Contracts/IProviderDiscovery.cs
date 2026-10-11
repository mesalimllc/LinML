namespace LinML.Contracts;

/// <summary>
/// Discovers what execution providers are available in the runtime.
/// Discovery reports visibility; it does not prove readiness.
/// </summary>
public interface IProviderDiscovery
{
    /// <summary>Discover available providers and hardware evidence.</summary>
    ProviderDiscoveryReport Discover();
}
