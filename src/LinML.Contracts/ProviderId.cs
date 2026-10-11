namespace LinML.Contracts;

/// <summary>
/// Identifies an execution provider (e.g., CPU, CUDA, TensorRT).
/// </summary>
public sealed record ProviderId(string Value)
{
    public static readonly ProviderId Cpu = new("CPUExecutionProvider");
    public static readonly ProviderId Cuda = new("CUDAExecutionProvider");
    public static readonly ProviderId Tensorrt = new("TensorrtExecutionProvider");
    public static readonly ProviderId TensorrtRtx = new("NvTensorRTRTXExecutionProvider");
    public static readonly ProviderId OpenVino = new("OpenVINOExecutionProvider");
    public static readonly ProviderId Qnn = new("QNNExecutionProvider");
    public static readonly ProviderId MigraphX = new("MIGraphXExecutionProvider");

    public override string ToString() => Value;
}
