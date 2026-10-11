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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(provider);

        _logger.LogInformation("Creating session for model {Model} with provider {Provider}",
            model.Path, provider);

        var sessionOptions = new SessionOptions();
        sessionOptions.EnableProfiling = enableProfiling;

        // Configure provider
        switch (provider.Value)
        {
            case "CPUExecutionProvider":
                // CPU is the default, no explicit append needed
                break;

            case "CUDAExecutionProvider":
                var cudaOptions = new OrtCUDAProviderOptions();
                // Apply any custom options
                if (options is not null)
                {
                    var dict = new Dictionary<string, string>();
                    foreach (var kvp in options)
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                    cudaOptions.UpdateOptions(dict);
                }
                sessionOptions.AppendExecutionProvider_CUDA(cudaOptions);
                break;

            default:
                throw new NotSupportedException($"Provider '{provider}' is not supported by this factory.");
        }

        // Create session
        var session = new InferenceSession(model.Path, sessionOptions);

        return new OnnxInferenceSession(session, model, provider, _logger, enableProfiling);
    }
}
