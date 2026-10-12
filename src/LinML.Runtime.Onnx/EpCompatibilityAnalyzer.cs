using LinML.Contracts;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Analyzes ONNX model graph operators, opset versions, and tensor dimensions
/// to generate actionable execution provider compatibility advisories and warnings.
/// </summary>
internal static class EpCompatibilityAnalyzer
{
    private static readonly HashSet<string> TensorRtUnsupportedOrControlFlowOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "NonMaxSuppression",
        "Loop",
        "If",
        "Scan",
        "StringNormalizer",
        "RegexFullMatch",
        "TfIdfVectorizer",
        "Unique",
        "Multinomial",
        "Col2Im"
    };

    public static List<CompatibilityFinding> Analyze(
        long irVersion,
        IReadOnlyList<OpsetImport> opsets,
        IReadOnlyList<ModelTensorMetadata> inputs,
        IReadOnlyList<string> operators)
    {
        var findings = new List<CompatibilityFinding>();
        var opsSet = new HashSet<string>(operators, StringComparer.OrdinalIgnoreCase);

        // 1. CUDAExecutionProvider checks
        if (opsSet.Contains("Conv") || opsSet.Contains("ConvTranspose"))
        {
            findings.Add(new CompatibilityFinding
            {
                Provider = ProviderId.Cuda,
                Severity = "Warning",
                Message = "Model contains convolution operations ('Conv'). CUDAExecutionProvider requires NVIDIA cuDNN (libcudnn.so.9) installed on the system."
            });
        }

        if (inputs.Any(i => string.Equals(i.DataType, "float16", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(new CompatibilityFinding
            {
                Provider = ProviderId.Cuda,
                Severity = "Info",
                Message = "Model inputs use FP16 (float16). Ensure host GPU supports native FP16 Tensor Cores for peak throughput."
            });
        }

        // 2. TensorrtExecutionProvider checks
        bool hasDynamicDimensions = inputs.Any(i => i.IsDynamic || i.Shape.Any(dim => dim < 0));
        if (hasDynamicDimensions)
        {
            findings.Add(new CompatibilityFinding
            {
                Provider = ProviderId.Tensorrt,
                Severity = "Warning",
                Message = "Model contains dynamic dimensions. TensorRT compiles static execution engines; specify '--ep-option trt:trt_profile_min_shapes=...' and '--ep-option trt:trt_engine_cache_enable=1' for optimal caching."
            });
        }

        foreach (var op in operators)
        {
            if (TensorRtUnsupportedOrControlFlowOps.Contains(op))
            {
                findings.Add(new CompatibilityFinding
                {
                    Provider = ProviderId.Tensorrt,
                    Severity = "Warning",
                    Message = $"Operator '{op}' is known to have limited TensorRT support or requires control-flow partitioning back to CPU."
                });
            }
        }

        // 3. OpenVINOExecutionProvider checks
        var mainOpset = opsets.FirstOrDefault(o => string.IsNullOrEmpty(o.Domain) || o.Domain == "ai.onnx");
        if (mainOpset != null && mainOpset.Version > 21)
        {
            findings.Add(new CompatibilityFinding
            {
                Provider = ProviderId.OpenVino,
                Severity = "Warning",
                Message = $"Model uses ONNX opset {mainOpset.Version}. OpenVINO EP supports up to opset 21; unverified operators may fall back to CPU."
            });
        }

        // 4. MIGraphXExecutionProvider checks
        if (opsSet.Contains("Loop") || opsSet.Contains("If") || opsSet.Contains("Scan"))
        {
            findings.Add(new CompatibilityFinding
            {
                Provider = ProviderId.MigraphX,
                Severity = "Warning",
                Message = "Model contains dynamic control flow operators ('Loop'/'If'). MIGraphX does not natively execute recursion and will partition to CPU."
            });
        }

        return findings;
    }
}
