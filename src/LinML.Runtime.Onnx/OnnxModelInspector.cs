using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using System.Security.Cryptography;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Inspects ONNX models without executing them, combining native ONNX Runtime session metadata
/// with streaming Protocol Buffer graph parsing and execution provider compatibility analysis.
/// </summary>
public sealed class OnnxModelInspector : IModelInspector
{
    private readonly ILogger<OnnxModelInspector> _logger;

    public OnnxModelInspector(ILogger<OnnxModelInspector>? logger = null)
    {
        _logger = logger ?? NullLogger<OnnxModelInspector>.Instance;
    }

    public async Task<ModelInspectionReport> InspectAsync(
        string modelPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        string fullPath = Path.GetFullPath(modelPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Model file not found: {fullPath}", fullPath);
        }

        _logger.LogDebug("Inspecting ONNX model at {Path}", fullPath);

        // 1. Identify model (SHA-256 and byte length)
        byte[] modelBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var modelIdentity = new ModelIdentity
        {
            Path = fullPath,
            Sha256 = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant(),
            SizeBytes = modelBytes.Length,
            Name = Path.GetFileNameWithoutExtension(fullPath)
        };

        // 2. Parse ONNX Protobuf structure
        OnnxProtobufReader.ParsedProto proto;
        using (var memStream = new MemoryStream(modelBytes, writable: false))
        {
            proto = OnnxProtobufReader.Parse(memStream);
        }

        var opsetImports = proto.OpsetImports
            .Select(o => new OpsetImport { Domain = o.Domain, Version = o.Version })
            .ToList();

        // 3. Extract Node Metadata using InferenceSession
        var inputs = new List<ModelTensorMetadata>();
        var outputs = new List<ModelTensorMetadata>();
        var metadataProps = new Dictionary<string, string>(proto.MetadataProps, StringComparer.Ordinal);
        string? producerName = proto.ProducerName;
        string? producerVersion = proto.ProducerVersion;
        string? domain = proto.Domain;
        long modelVersion = proto.ModelVersion;
        string? description = proto.DocString;

        try
        {
            using var sessionOptions = new SessionOptions();
            sessionOptions.AppendExecutionProvider_CPU(0);
            sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL;

            using var session = new InferenceSession(fullPath, sessionOptions);

            var meta = session.ModelMetadata;
            if (!string.IsNullOrWhiteSpace(meta.ProducerName)) producerName = meta.ProducerName;
            if (!string.IsNullOrWhiteSpace(meta.Domain)) domain = meta.Domain;
            if (meta.Version > 0) modelVersion = meta.Version;
            if (!string.IsNullOrWhiteSpace(meta.Description)) description = meta.Description;

            foreach (var kv in meta.CustomMetadataMap)
            {
                metadataProps[kv.Key] = kv.Value;
            }

            foreach (var (name, node) in session.InputMetadata)
            {
                inputs.Add(CreateModelTensor(name, node));
            }

            foreach (var (name, node) in session.OutputMetadata)
            {
                outputs.Add(CreateModelTensor(name, node));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open InferenceSession to read runtime node metadata; relying on protobuf structure.");
            inputs.Clear();
            outputs.Clear();
            inputs.AddRange(proto.Inputs.Select(CreateModelTensor));
            outputs.AddRange(proto.Outputs.Select(CreateModelTensor));
        }

        // 4. Graph structure
        var graph = new ModelGraphMetadata
        {
            NodeCount = proto.NodeCount,
            Operators = proto.Operators
        };

        // 5. Execution Provider Compatibility Analysis
        var warnings = EpCompatibilityAnalyzer.Analyze(
            proto.IrVersion,
            opsetImports,
            inputs,
            proto.Operators);

        return new ModelInspectionReport
        {
            Model = modelIdentity,
            IrVersion = proto.IrVersion,
            OpsetImports = opsetImports,
            ProducerName = producerName,
            ProducerVersion = producerVersion,
            Domain = domain,
            ModelVersion = modelVersion,
            Description = description,
            MetadataProps = metadataProps,
            Inputs = inputs,
            Outputs = outputs,
            Graph = graph,
            CompatibilityWarnings = warnings
        };
    }

    private static ModelTensorMetadata CreateModelTensor(OnnxProtobufReader.ParsedTensor tensor)
    {
        return new ModelTensorMetadata
        {
            Name = tensor.Name,
            DataType = MapTypeName(tensor.ElementType),
            Shape = tensor.Dimensions,
            IsDynamic = tensor.Dimensions.Any(dim => dim < 0) || tensor.SymbolicDimensions.Any(s => !string.IsNullOrEmpty(s)),
            SymbolicDimensions = tensor.SymbolicDimensions
        };
    }

    private static string MapTypeName(int elementType) => elementType switch
    {
        1 => "float32", 2 => "uint8", 3 => "int8", 5 => "int16", 6 => "int32",
        7 => "int64", 8 => "string", 9 => "bool", 10 => "float16", 11 => "float64",
        _ => "unknown"
    };

    private static ModelTensorMetadata CreateModelTensor(string name, NodeMetadata node)
    {
        var shape = node.Dimensions?.ToList() ?? [];
        var symbolic = node.SymbolicDimensions?.ToList() ?? [];
        bool isDynamic = shape.Any(dim => dim < 0) || symbolic.Any(s => !string.IsNullOrEmpty(s) && !int.TryParse(s, out _));

        return new ModelTensorMetadata
        {
            Name = name,
            DataType = MapTypeName(node.ElementType),
            Shape = shape,
            IsDynamic = isDynamic,
            SymbolicDimensions = symbolic
        };
    }

    private static string MapTypeName(Type? type)
    {
        if (type == null) return "unknown";
        if (type == typeof(float)) return "float32";
        if (type == typeof(double)) return "float64";
        if (type == typeof(Half)) return "float16";
        if (type == typeof(long)) return "int64";
        if (type == typeof(int)) return "int32";
        if (type == typeof(short)) return "int16";
        if (type == typeof(sbyte)) return "int8";
        if (type == typeof(byte)) return "uint8";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(string)) return "string";
        return type.Name.ToLowerInvariant();
    }
}
