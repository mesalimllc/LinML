using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx.Providers;

/// <summary>
/// CUDA provider adapter: wraps CUDA-specific configuration and readiness checks.
/// </summary>
public sealed class CudaProviderAdapter
{
    private readonly ILogger<CudaProviderAdapter> _logger;

    public CudaProviderAdapter(ILogger<CudaProviderAdapter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProviderId ProviderId => Contracts.ProviderId.Cuda;

    /// <summary>Configure CUDA-specific session options.</summary>
    public OrtCUDAProviderOptions CreateProviderOptions(IReadOnlyDictionary<string, string>? customOptions = null)
    {
        var options = new OrtCUDAProviderOptions();

        if (customOptions is not null)
        {
            var dict = new Dictionary<string, string>();
            foreach (var kvp in customOptions)
            {
                dict[kvp.Key] = kvp.Value;
            }
            options.UpdateOptions(dict);
        }

        _logger.LogDebug("CUDA provider options created");
        return options;
    }
}
