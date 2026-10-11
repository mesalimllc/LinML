using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Diagnostics utilities for ONNX Runtime: version info, provider listing, error classification.
/// </summary>
public static class OnnxRuntimeDiagnostics
{
    /// <summary>Get the ORT runtime version.</summary>
    public static string GetRuntimeVersion()
    {
        var assembly = typeof(OrtEnv).Assembly;
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>List all available execution providers in the loaded ORT runtime.</summary>
    public static IReadOnlyList<string> GetAvailableProviders()
    {
        return OrtEnv.Instance().GetAvailableProviders();
    }

    /// <summary>
    /// Classify an ORT exception into a failure phase.
    /// </summary>
    public static ReadinessPhase ClassifyFailure(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();

        if (message.Contains("cuda") || message.Contains("cudnn") || message.Contains("gpu"))
        {
            // Likely a load or append failure due to missing CUDA dependencies
            if (message.Contains("load") || message.Contains("dlopen") || message.Contains("library"))
            {
                return ReadinessPhase.Load;
            }
            return ReadinessPhase.Append;
        }

        if (message.Contains("session") || message.Contains("model"))
        {
            return ReadinessPhase.Smoke;
        }

        // Default: assume load phase (earliest failure point)
        return ReadinessPhase.Load;
    }
}
