namespace LinML.Contracts;

/// <summary>
/// Identifies a model by path and checksum. Used to track model identity
/// through selection and execution.
/// </summary>
public sealed record ModelIdentity
{
    public required string Path { get; init; }
    public required string Sha256 { get; init; }
    public long SizeBytes { get; init; }
    public string? Name { get; init; }
}
