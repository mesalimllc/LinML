namespace LinML.Contracts;

/// <summary>
/// An active inference session. Must be disposed.
/// </summary>
public interface IInferenceSession : IDisposable
{
    /// <summary>The provider this session is using.</summary>
    ProviderId Provider { get; }

    /// <summary>The model this session loaded.</summary>
    ModelIdentity Model { get; }

    /// <summary>Run inference with the given inputs.</summary>
    IReadOnlyList<TensorOutput> Run(
        IReadOnlyList<TensorInput> inputs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// End ORT profiling and return per-node provider placement.
    /// Returns an empty list when the session was not created with profiling enabled.
    /// </summary>
    IReadOnlyList<NodePlacement> EndProfiling();

    /// <summary>Path of the ORT profile file, once <see cref="EndProfiling"/> has run.</summary>
    string? ProfilePath { get; }
}
