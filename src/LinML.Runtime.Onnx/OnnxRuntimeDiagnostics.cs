using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Diagnostics utilities for ONNX Runtime.
/// </summary>
public static class OnnxRuntimeDiagnostics
{
    /// <summary>Get the ORT runtime version.</summary>
    public static string GetRuntimeVersion()
    {
        var assembly = typeof(OrtEnv).Assembly;
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
