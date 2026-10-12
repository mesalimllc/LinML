using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Text.Json;

namespace LinML.Runtime.Onnx;

/// <summary>
/// ONNX Runtime inference session implementation.
/// </summary>
public sealed class OnnxInferenceSession : IInferenceSession
{
    private readonly InferenceSession _session;
    private readonly ILogger _logger;
    private readonly bool _profilingEnabled;
    private bool _disposed;

    public OnnxInferenceSession(
        InferenceSession session,
        ModelIdentity model,
        ProviderId provider,
        ILogger logger,
        bool profilingEnabled = false)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profilingEnabled = profilingEnabled;
    }

    public ProviderId Provider { get; }
    public ModelIdentity Model { get; }
    public string? ProfilePath { get; private set; }

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

            namedInputs.Add(CreateNamedOnnxValue(input));
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

    private static NamedOnnxValue CreateNamedOnnxValue(TensorInput input)
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

        var shapeArray = input.Shape.ToArray();
        return input.DataType switch
        {
            TensorDataType.Int64 => NamedOnnxValue.CreateFromTensor(
                input.Name, new DenseTensor<long>(input.Data.Select(f => (long)f).ToArray(), shapeArray)),
            TensorDataType.Int32 => NamedOnnxValue.CreateFromTensor(
                input.Name, new DenseTensor<int>(input.Data.Select(f => (int)f).ToArray(), shapeArray)),
            _ => NamedOnnxValue.CreateFromTensor(
                input.Name, new DenseTensor<float>(input.Data.ToArray(), shapeArray))
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.Dispose();
    }

    public IReadOnlyList<NodePlacement> EndProfiling()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_profilingEnabled)
        {
            return [];
        }

        ProfilePath = _session.EndProfiling();
        _logger.LogInformation("ORT profile written to {ProfilePath}", ProfilePath);

        var placements = new List<NodePlacement>();
        using var doc = JsonDocument.Parse(File.ReadAllText(ProfilePath));
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("cat", out var cat) && cat.GetString() == "Node"
                && element.TryGetProperty("args", out var args)
                && args.TryGetProperty("provider", out var provider)
                && element.TryGetProperty("name", out var name))
            {
                placements.Add(new NodePlacement
                {
                    NodeName = name.GetString() ?? "",
                    Provider = provider.GetString() ?? ""
                });
            }
        }

        return placements;
    }
}
