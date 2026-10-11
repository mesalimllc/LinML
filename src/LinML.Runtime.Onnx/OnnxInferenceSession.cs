using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LinML.Runtime.Onnx;

/// <summary>
/// ONNX Runtime inference session implementation.
/// </summary>
public sealed class OnnxInferenceSession : IInferenceSession
{
    private readonly InferenceSession _session;
    private readonly ILogger _logger;
    private bool _disposed;

    public OnnxInferenceSession(
        InferenceSession session,
        ModelIdentity model,
        ProviderId provider,
        ILogger logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ProviderId Provider { get; }
    public ModelIdentity Model { get; }

    public IReadOnlyList<TensorOutput> Run(
        IReadOnlyList<TensorInput> inputs,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(inputs);

        _logger.LogDebug("Running inference with {InputCount} inputs on provider {Provider}",
            inputs.Count, Provider);

        // Build named inputs
        var namedInputs = new List<NamedOnnxValue>();
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tensor = CreateTensor(input);
            namedInputs.Add(NamedOnnxValue.CreateFromTensor(input.Name, tensor));
        }

        // Run inference
        using var results = _session.Run(namedInputs);

        // Extract outputs
        var outputs = new List<TensorOutput>();
        foreach (var result in results)
        {
            var tensor = result.AsTensor<float>();
            var shape = tensor.Dimensions.ToArray();
            var data = tensor.ToArray();

            outputs.Add(new TensorOutput
            {
                Name = result.Name,
                DataType = TensorDataType.Float32,
                Shape = shape,
                Data = data
            });
        }

        return outputs;
    }

    private static DenseTensor<float> CreateTensor(TensorInput input)
    {
        // Validate data length matches shape
        var expectedLength = 1;
        foreach (var dim in input.Shape)
        {
            expectedLength *= dim;
        }

        if (input.Data.Count != expectedLength)
        {
            throw new ArgumentException(
                $"Tensor '{input.Name}' has {input.Data.Count} elements but shape {string.Join("x", input.Shape)} requires {expectedLength}.");
        }

        return new DenseTensor<float>(input.Data.ToArray(), input.Shape.ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
    }
}
