namespace LinML.Contracts;

/// <summary>
/// Comprehensive model inspection report containing ONNX metadata, graph topology,
/// tensor dimensions, and execution provider compatibility warnings.
/// </summary>
public sealed record ModelInspectionReport
{
    public required ModelIdentity Model { get; init; }
    public long IrVersion { get; init; }
    public List<OpsetImport> OpsetImports { get; init; } = [];
    public string? ProducerName { get; init; }
    public string? ProducerVersion { get; init; }
    public string? Domain { get; init; }
    public long ModelVersion { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string> MetadataProps { get; init; } = [];
    public List<ModelTensorMetadata> Inputs { get; init; } = [];
    public List<ModelTensorMetadata> Outputs { get; init; } = [];
    public ModelGraphMetadata Graph { get; init; } = new();
    public List<CompatibilityFinding> CompatibilityWarnings { get; init; } = [];
}

/// <summary>
/// Opset import entry mapping an operator domain to a version.
/// </summary>
public sealed record OpsetImport
{
    public string Domain { get; init; } = "";
    public long Version { get; init; }
}

/// <summary>
/// Input or output tensor definition with dimensions and dynamic status.
/// </summary>
public sealed record ModelTensorMetadata
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public List<int> Shape { get; init; } = [];
    public bool IsDynamic { get; init; }
    public List<string> SymbolicDimensions { get; init; } = [];
}

/// <summary>
/// Graph summary with node count and distinct operator types.
/// </summary>
public sealed record ModelGraphMetadata
{
    public int NodeCount { get; init; }
    public List<string> Operators { get; init; } = [];
}

/// <summary>
/// Execution provider compatibility advisory or warning.
/// </summary>
public sealed record CompatibilityFinding
{
    public required ProviderId Provider { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
}
