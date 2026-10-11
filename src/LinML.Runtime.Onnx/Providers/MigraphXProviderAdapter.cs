using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// MIGraphX provider adapter: wraps AMD MIGraphX-specific configuration.
/// </summary>
public sealed class MigraphXProviderAdapter
{
    private readonly ILogger<MigraphXProviderAdapter> _logger;

    public MigraphXProviderAdapter(ILogger<MigraphXProviderAdapter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProviderId ProviderId => Contracts.ProviderId.MigraphX;

    /// <summary>Configure MIGraphX-specific session options.</summary>
    public void ConfigureSessionOptions(
        SessionOptions sessionOptions,
        IReadOnlyDictionary<string, string>? options = null)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        var dict = options is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(options);
        sessionOptions.AppendExecutionProvider("MIGraphXExecutionProvider", dict);
        _logger.LogDebug("MIGraphX provider options configured");
    }
}