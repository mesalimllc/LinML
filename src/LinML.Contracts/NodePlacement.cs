namespace LinML.Contracts;

/// <summary>
/// Per-node execution placement from ORT profiling: which provider ran each node.
/// Proves "every node ran where claimed" (correctness rule: model runnable ≠
/// every node ran on GPU).
/// </summary>
public sealed record NodePlacement
{
    /// <summary>Node name as reported in the ORT profile (e.g. MatMul_0/MatMulAddFusion).</summary>
    public required string NodeName { get; init; }

    /// <summary>Execution provider that ran the node (e.g. CUDAExecutionProvider).</summary>
    public required string Provider { get; init; }
}
