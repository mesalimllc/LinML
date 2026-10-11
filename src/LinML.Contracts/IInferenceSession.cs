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
}
