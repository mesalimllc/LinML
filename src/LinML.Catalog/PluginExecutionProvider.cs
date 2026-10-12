using System.Runtime.InteropServices;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Catalog;

/// <summary>A plugin EP vendored from a pinned wheel and registered with ORT.</summary>
internal sealed class PluginExecutionProvider : IExecutionProvider
{
    private readonly PluginPackageSpec _spec;
    private readonly string _installRoot;
    private readonly PluginInstaller _installer;
    private readonly IOrtRegistrar _registrar;
    private readonly Func<bool> _isSupportedPlatform;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    // Keep dependency handles alive for as long as ORT may resolve the plugin.
    private readonly List<IntPtr> _bundledLibraryHandles = [];

    internal PluginExecutionProvider(
        PluginPackageSpec spec,
        string installRoot,
        PluginInstaller installer,
        IOrtRegistrar registrar,
        Func<bool>? isSupportedPlatform = null)
    {
        _spec = spec;
        _installRoot = installRoot;
        _installer = installer;
        _registrar = registrar;
        _isSupportedPlatform = isSupportedPlatform ?? IsLinuxX64;
    }

    public ProviderId Id => _spec.Id;

    public ProviderSource Source => ProviderSource.Plugin;

    public string? PinnedVersion => _spec.Version;

    public string? LibraryPath => File.Exists(_spec.LibraryPath(_installRoot)) ? _spec.LibraryPath(_installRoot) : null;

    public ProviderReadyState ReadyState
    {
        get
        {
            if (UnsupportedReason() is not null)
            {
                return ProviderReadyState.Unsupported;
            }

            if (GetDevices().Count > 0)
            {
                return ProviderReadyState.Ready;
            }

            return LibraryPath is null ? ProviderReadyState.NotPresent : ProviderReadyState.NotReady;
        }
    }

    // Devices for this EP name from whichever registration made them, the
    // catalog's or one made earlier in the process (e.g. LINML_EP_LIBRARIES
    // or the host): the catalog never registers a second copy, because ORT
    // rejects an append whose devices come from different EP factories.
    public IReadOnlyList<OrtEpDevice> GetDevices() => _registrar.GetDevices(_spec.Id.Value);

    public bool TryRegister(out string? failureReason)
    {
        failureReason = UnsupportedReason();
        if (failureReason is not null)
        {
            return false;
        }

        if (!_registrar.IsRegistered(_spec.RegistrationName) && GetDevices().Count > 0)
        {
            return true; // already registered elsewhere in this process
        }

        string? library = LibraryPath;
        if (library is null)
        {
            failureReason = $"{Id} is not vendored at {_spec.InstallDirectory(_installRoot)}.";
            return false;
        }

        try
        {
            // The wheel's bundled SONAME dependencies are not visible to the
            // native loader merely because they share the plugin directory.
            // Load them explicitly before ORT loads the plugin.
            _bundledLibraryHandles.AddRange(
                PluginInstaller.LoadBundledLibraries(_spec.InstallDirectory(_installRoot), _spec.LibraryFileName));
            _registrar.Register(_spec.RegistrationName, library);
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or ArgumentException or IOException or DllNotFoundException)
        {
            failureReason = $"Registering {library} failed: {ex.Message}";
            return false;
        }
    }

    public async Task<ProviderEnsureResult> EnsureReadyAsync(EnsureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!_isSupportedPlatform())
        {
            return Result(ProviderEnsureStatus.NotAvailable, UnsupportedReason()!);
        }

        if (_registrar.OrtVersion < _spec.MinimumOrtVersion)
        {
            return Result(ProviderEnsureStatus.AbiIncompatible, UnsupportedReason()!);
        }

        await _ensureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<OrtEpDevice> existing = GetDevices();
            if (existing.Count > 0)
            {
                return new ProviderEnsureResult(
                    Id, ProviderEnsureStatus.Ready, $"{Id} already registered with {existing.Count} EP device(s).", existing);
            }

            bool downloaded = false;
            if (LibraryPath is null)
            {
                if (!options.AllowDownloads)
                {
                    return Result(
                        ProviderEnsureStatus.DownloadDisallowed,
                        $"{Id} {_spec.Version} is not vendored at {_spec.InstallDirectory(_installRoot)} and downloads are not allowed.");
                }

                try
                {
                    downloaded = await _installer.InstallAsync(_spec, _installRoot, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return Result(ProviderEnsureStatus.Failed, $"Vendoring {Id} {_spec.Version} failed: {ex.Message}");
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // HttpClient.Timeout surfaces as TaskCanceledException; caller cancellation still propagates.
                    return Result(ProviderEnsureStatus.Failed, $"Vendoring {Id} {_spec.Version} timed out: {ex.Message}");
                }
            }

            if (!TryRegister(out string? failure))
            {
                return Result(ProviderEnsureStatus.Failed, failure!);
            }

            IReadOnlyList<OrtEpDevice> devices = GetDevices();
            if (devices.Count == 0)
            {
                string detail = Id == ProviderId.TensorrtRtx && IsWsl()
                    ? $"{Id} is registered, but in WSL2 virtual PCI mapping blocks device enumeration (see docs/compatibility.md). Use custom build or bare-metal Linux."
                    : $"{Id} is registered but exposes no EP devices on this host.";
                return Result(ProviderEnsureStatus.NoDevices, detail);
            }

            return new ProviderEnsureResult(
                Id,
                downloaded ? ProviderEnsureStatus.Downloaded : ProviderEnsureStatus.Ready,
                $"{Id} {_spec.Version} ready with {devices.Count} EP device(s).",
                devices);
        }
        finally
        {
            _ensureGate.Release();
        }
    }

    private string? UnsupportedReason()
    {
        if (!_isSupportedPlatform())
        {
            return $"{Id} plugin {_spec.Version} is available for linux-x64 only.";
        }

        Version ort = _registrar.OrtVersion;
        return ort < _spec.MinimumOrtVersion
            ? $"{Id} plugin {_spec.Version} needs ONNX Runtime {_spec.MinimumOrtVersion} or newer; loaded ONNX Runtime is {ort}."
            : null;
    }

    private ProviderEnsureResult Result(ProviderEnsureStatus status, string detail) => new(Id, status, detail, []);

    private static bool IsLinuxX64() =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static bool IsWsl()
    {
        try
        {
            return File.Exists("/proc/sys/fs/binfmt_misc/WSLInterop")
                || (File.Exists("/proc/version") && File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
