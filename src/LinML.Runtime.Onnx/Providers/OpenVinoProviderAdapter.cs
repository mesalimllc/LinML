using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// OpenVINO provider adapter: wraps OpenVINO-specific configuration.
/// </summary>
public sealed class OpenVinoProviderAdapter
{
    private readonly ILogger<OpenVinoProviderAdapter> _logger;

    public OpenVinoProviderAdapter(ILogger<OpenVinoProviderAdapter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProviderId ProviderId => Contracts.ProviderId.OpenVino;

    /// <summary>Configure OpenVINO-specific session options.</summary>
    public void ConfigureSessionOptions(
        SessionOptions sessionOptions,
        IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        var dict = options is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(options);
        sessionOptions.AppendExecutionProvider("OpenVINOExecutionProvider", dict);
        _logger.LogDebug("OpenVINO provider options configured");
    }
}