namespace LinML.Contracts;

/// <summary>
/// A request to run inference: which model, which inputs, which provider preference.
/// </summary>
public sealed record InferenceRequest
{
    public required ModelIdentity Model { get; init; }
    public required IReadOnlyList<TensorInput> Inputs { get; init; }
    public ProviderId? PreferredProvider { get; init; }
    public bool RequireProvider { get; init; }
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>A named tensor input with explicit type, shape, and data.</summary>
public sealed record TensorInput
{
    public required string Name { get; init; }
    public required TensorDataType DataType { get; init; }
    public required IReadOnlyList<int> Shape { get; init; }
    public required IReadOnlyList<float> Data { get; init; }
}

public enum TensorDataType
{
    Float32,
    Float16,
    Int64,
    Int32
}
