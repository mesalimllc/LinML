using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Catalog;

/// <summary>Where a provider comes from.</summary>
public enum ProviderSource
{
    /// <summary>Compiled into the loaded ONNX Runtime build (e.g. CPU, CUDA).</summary>
    BuiltIn,

    /// <summary>A plugin EP library vendored and registered at run time.</summary>
    Plugin,
}

/// <summary>
/// Readiness of a provider on this host. Mirrors the Windows ML
/// <c>ExecutionProviderReadyState</c>, plus <see cref="Unsupported"/>.
/// </summary>
public enum ProviderReadyState
{
    /// <summary>Registered (or built in) and exposing at least one EP device.</summary>
    Ready,

    /// <summary>
    /// Vendored but not registered yet, or registered without EP devices.
    /// <see cref="IExecutionProvider.EnsureReadyAsync"/> reports which.
    /// </summary>
    NotReady,

    /// <summary>The plugin is not vendored on this host.</summary>
    NotPresent,

    /// <summary>Cannot work here: wrong OS or architecture, or the loaded ORT is too old.</summary>
    Unsupported,
}

/// <summary>Options for <see cref="IExecutionProvider.EnsureReadyAsync"/>.</summary>
/// <param name="AllowDownloads">Whether a missing plugin may be downloaded.</param>
public sealed record EnsureOptions(bool AllowDownloads);

/// <summary>Outcome of <see cref="IExecutionProvider.EnsureReadyAsync"/>.</summary>
public enum ProviderEnsureStatus
{
    /// <summary>Already vendored and registered, with EP devices.</summary>
    Ready,

    /// <summary>Vendored during this call, then registered, with EP devices.</summary>
    Downloaded,

    /// <summary>Registered, but the EP exposes zero devices (e.g. no matching hardware).</summary>
    NoDevices,

    /// <summary>The plugin needs a newer ONNX Runtime than the one loaded.</summary>
    AbiIncompatible,

    /// <summary>The plugin is not vendored and downloads were not allowed.</summary>
    DownloadDisallowed,

    /// <summary>The provider cannot work on this OS or architecture.</summary>
    NotAvailable,

    /// <summary>Download, verification, extraction or registration failed.</summary>
    Failed,
}

/// <summary>Result of <see cref="IExecutionProvider.EnsureReadyAsync"/>.</summary>
/// <param name="Id">The provider.</param>
/// <param name="Status">What happened.</param>
/// <param name="Detail">Human-readable detail, safe for logs and UI.</param>
/// <param name="Devices">EP devices exposed after the call; empty unless Ready or Downloaded.</param>
public sealed record ProviderEnsureResult(
    ProviderId Id,
    ProviderEnsureStatus Status,
    string Detail,
    IReadOnlyList<OrtEpDevice> Devices)
{
    /// <summary>True when the provider can be appended to a session.</summary>
    public bool IsReady => Status is ProviderEnsureStatus.Ready or ProviderEnsureStatus.Downloaded;
}

/// <summary>Result of <see cref="SessionOptionsCatalogExtensions.AppendFromCatalog"/>.</summary>
/// <param name="Appended">Whether the provider was appended.</param>
/// <param name="Provider">The provider.</param>
/// <param name="FailureReason">Why it was not appended, when <paramref name="Appended"/> is false.</param>
public sealed record ProviderAppendResult(bool Appended, ProviderId Provider, string? FailureReason = null);
