namespace LinML.Contracts;

/// <summary>
/// Result of provider discovery: what providers are visible to the runtime,
/// what hardware was detected, and any diagnostics.
/// </summary>
public sealed record ProviderDiscoveryReport
{
    /// <summary>Provider identities available in the loaded ORT runtime.</summary>
    public required IReadOnlyList<ProviderId> AvailableProviders { get; init; }

    /// <summary>Device identities (GPU model, VRAM) when detectable.</summary>
    public IReadOnlyList<DeviceIdentity> Devices { get; init; } = [];

    /// <summary>Hardware evidence gathered during discovery (driver version, nvidia-smi output, etc.).</summary>
    public IReadOnlyList<HardwareEvidence> HardwareEvidence { get; init; } = [];

    /// <summary>ORT runtime version string.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>Diagnostics from the discovery process.</summary>
    public IReadOnlyList<DiagnosticEntry> Diagnostics { get; init; } = [];
}

/// <summary>Identifies a compute device (e.g., GPU 0).</summary>
public sealed record DeviceIdentity
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Vendor { get; init; }
    public long? MemoryBytes { get; init; }
}

/// <summary>Evidence of hardware presence (does not prove provider availability).</summary>
public sealed record HardwareEvidence
{
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public string? Source { get; init; }
}

/// <summary>A diagnostic message from any phase.</summary>
public sealed record DiagnosticEntry
{
    public required string Phase { get; init; }
    public required string Message { get; init; }
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Info;
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}
