namespace LinML.Contracts;

/// <summary>
/// Creates inference sessions for a specific provider.
/// </summary>
public interface IInferenceSessionFactory
{
    /// <summary>Create a session for the given model and provider.</summary>
    /// <param name="model">The model to load.</param>
    /// <param name="provider">The provider to use.</param>
    /// <param name="options">Provider-specific options.</param>
    /// <param name="enableProfiling">Enable ORT profiling (see <see cref="IInferenceSession.EndProfiling"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A session ready for inference, or throws on failure.</returns>
    IInferenceSession CreateSession(
        ModelIdentity model,
        ProviderId provider,
        IReadOnlyDictionary<string, string>? options = null,
        bool enableProfiling = false,
        CancellationToken cancellationToken = default);
}
