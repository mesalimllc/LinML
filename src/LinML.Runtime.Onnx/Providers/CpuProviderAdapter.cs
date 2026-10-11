using LinML.Contracts;
using Microsoft.Extensions.Logging;

namespace LinML.Runtime.Onnx.Providers;

/// <summary>
/// CPU provider adapter: wraps CPU-specific configuration and readiness checks.
/// </summary>
public sealed class CpuProviderAdapter
{
    private readonly ILogger<CpuProviderAdapter> _logger;

    public CpuProviderAdapter(ILogger<CpuProviderAdapter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>CPU provider is always available in standard ORT builds.</summary>
    public ProviderId ProviderId => Contracts.ProviderId.Cpu;

    /// <summary>Configure CPU-specific session options.</summary>
    public void ConfigureSessionOptions(Microsoft.ML.OnnxRuntime.SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // CPU is the default provider; no explicit append needed.
        // Could set thread count here if needed.
        _logger.LogDebug("CPU provider configured");
    }
}
