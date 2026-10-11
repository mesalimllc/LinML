using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Catalog;

/// <summary>
/// Execution providers known on this host. The Linux counterpart to the
/// Windows ML <c>ExecutionProviderCatalog</c>; obtain it with
/// <see cref="ExecutionProviderCatalog.GetDefault"/>.
/// </summary>
public interface IExecutionProviderCatalog
{
    /// <summary>Every provider this catalog knows, whether or not it is ready.</summary>
    IReadOnlyList<IExecutionProvider> FindAllProviders();

    /// <summary>The provider with the given id, or null if the catalog does not know it.</summary>
    IExecutionProvider? Find(ProviderId id);
}

/// <summary>One execution provider in the catalog.</summary>
public interface IExecutionProvider
{
    /// <summary>The ORT execution provider name, e.g. <c>OpenVINOExecutionProvider</c>.</summary>
    ProviderId Id { get; }

    /// <summary>Built into ORT or a plugin library.</summary>
    ProviderSource Source { get; }

    /// <summary>Current readiness. Cheap; does not download or register.</summary>
    ProviderReadyState ReadyState { get; }

    /// <summary>Path of the plugin library when vendored; null for built-ins or when not present.</summary>
    string? LibraryPath { get; }

    /// <summary>The pinned plugin package version, e.g. <c>1.6.1</c>; null for built-ins.</summary>
    string? PinnedVersion { get; }

    /// <summary>
    /// Ensures the plugin is vendored (pinned wheel, sha256-verified, staged
    /// extract) and registered with <see cref="OrtEnv"/>. Downloads only when
    /// <see cref="EnsureOptions.AllowDownloads"/> is set. Idempotent and safe to
    /// call concurrently within a process.
    /// </summary>
    Task<ProviderEnsureResult> EnsureReadyAsync(EnsureOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers an already-vendored plugin with <see cref="OrtEnv"/>. Never
    /// downloads. Mirrors the Windows ML <c>TryRegister</c>.
    /// </summary>
    bool TryRegister(out string? failureReason);

    /// <summary>EP devices ORT exposes for this provider; empty when not registered.</summary>
    IReadOnlyList<OrtEpDevice> GetDevices();
}
