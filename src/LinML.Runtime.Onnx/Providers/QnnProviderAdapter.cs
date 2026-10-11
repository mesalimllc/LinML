using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// QNN provider adapter: wraps Qualcomm QNN-specific configuration.
/// </summary>
public sealed class QnnProviderAdapter
{
    private readonly ILogger<QnnProviderAdapter> _logger;

    public QnnProviderAdapter(ILogger<QnnProviderAdapter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProviderId ProviderId => Contracts.ProviderId.Qnn;

    /// <summary>Configure QNN-specific session options.</summary>
    public void ConfigureSessionOptions(
        SessionOptions sessionOptions,
        IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        var dict = options is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(options);
        sessionOptions.AppendExecutionProvider("QNNExecutionProvider", dict);
        _logger.LogDebug("QNN provider options configured");
    }
}