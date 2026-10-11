namespace LinML.Contracts;

/// <summary>
/// Identifies an execution provider (e.g., CPU, CUDA).
/// </summary>
public sealed record ProviderId(string Value)
{
    public static readonly ProviderId Cpu = new("CPUExecutionProvider");
    public static readonly ProviderId Cuda = new("CUDAExecutionProvider");

    public override string ToString() => Value;
}
