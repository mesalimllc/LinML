using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Creates ONNX Runtime inference sessions for specific providers.
/// </summary>
public sealed class OnnxSessionFactory : IInferenceSessionFactory
{
    private readonly ILogger<OnnxSessionFactory> _logger;

    public OnnxSessionFactory(ILogger<OnnxSessionFactory> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IInferenceSession CreateSession(
        ModelIdentity model,
        ProviderId provider,
        IReadOnlyDictionary<string, string>? options = null,
        bool enableProfiling = false,
        IReadOnlyDictionary<string, string>? epLibraries = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(provider);

        _logger.LogInformation("Creating session for model {Model} with provider {Provider}",
            model.Path, provider);

        using var sessionOptions = new SessionOptions();
        sessionOptions.EnableProfiling = enableProfiling;

        EpAppender.Append(sessionOptions, provider, options, epLibraries);

        // Create session
        var session = new InferenceSession(model.Path, sessionOptions);

        // Providers can leave a handled CUDA error latched on this thread; do not let
        // it fail the next provider's first kernel (see CudaStickyError).
        CudaStickyError.Clear();

        return new OnnxInferenceSession(session, model, provider, _logger, enableProfiling);
    }
}
