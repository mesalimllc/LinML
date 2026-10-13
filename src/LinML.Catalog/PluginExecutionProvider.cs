using System.Runtime.InteropServices;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Catalog;

/// <summary>A plugin EP vendored from a pinned wheel and registered with ORT.</summary>
internal sealed class PluginExecutionProvider : IExecutionProvider
{
    private readonly PluginPackageSpec _spec;
    private readonly PluginPackageSpec _wsl2Spec;
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
        Func<bool>? isSupportedPlatform = null,
        PluginPackageSpec? wsl2Spec = null)
    {
        _spec = spec;
        _wsl2Spec = wsl2Spec ?? KnownPlugins.TensorrtRtxWsl2;
        _installRoot = installRoot;
        _installer = installer;
        _registrar = registrar;
        _isSupportedPlatform = isSupportedPlatform ?? (() => SupportsHost(spec.Architecture, OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture));
    }

    public ProviderId Id => _spec.Id;

    public ProviderSource Source => ProviderSource.Plugin;

    public string? PinnedVersion => _spec.Version;

    public string? LibraryPath => SelectVariant() switch
    {
        Variant.Custom => CustomLibraryPath(),
        Variant.Wsl2 => WslVariantLibraryPath(),
        _ => StockLibraryPath(),
    };

    private enum Variant { Stock, Wsl2, Custom }

    // Only TensorrtRtx has variants. Highest precedence first: an explicit TRT_RTX_CUSTOM_EP file
    // (as the CLI honours it), the patched WSL2 build on a WSL2 GPU host unless opted out, then
    // the stock wheel.
    private Variant SelectVariant()
    {
        if (_spec.Id != ProviderId.TensorrtRtx)
        {
            return Variant.Stock;
        }

        if (CustomLibraryPath() is not null)
        {
            return Variant.Custom;
        }

        // The patched WSL2 build is x86_64 only; WSL2 on an ARM host uses the stock wheel.
        return IsWsl2() && !Wsl2OptedOut() && _spec.Architecture == "x64" ? Variant.Wsl2 : Variant.Stock;
    }

    private string? CustomLibraryPath()
    {
        if (_spec.Id != ProviderId.TensorrtRtx)
        {
            return null;
        }

        string? custom = EnvReader(TrtRtxCustomEpEnvVar);
        return !string.IsNullOrWhiteSpace(custom) && File.Exists(custom) ? custom : null;
    }

    internal static bool Wsl2OptedOut() =>
        EnvReader(Wsl2OptOutEnvVar)?.Trim().ToLowerInvariant() is "0" or "false" or "off" or "no";

    private string? StockLibraryPath() =>
        File.Exists(_spec.LibraryPath(_installRoot))
            ? _spec.LibraryPath(_installRoot)
            : (_installRoot == ExecutionProviderCatalog.DefaultInstallRoot ? _spec.FindLibrary() : null);

    // The stock wheel's runtime libraries, looked up like LibraryPath: strictly under this
    // catalog's root, widening to the default roots only for the default catalog.
    private string? StockRuntimeDirectory()
    {
        string own = _spec.LibraryPath(_installRoot);
        if (File.Exists(own))
        {
            return Path.GetDirectoryName(own);
        }

        return _installRoot == ExecutionProviderCatalog.DefaultInstallRoot
            ? KnownPlugins.WslVariantRuntimeDirectory()
            : null;
    }

    // The WSL2 build lives in <root>/ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep, wherever the root is.
    private bool IsWslVariant(string library) =>
        _spec.Id == ProviderId.TensorrtRtx
        && string.Equals(
            Path.GetFileName(Path.GetDirectoryName(library)),
            Path.GetFileName(_wsl2Spec.InstallSubdirectory),
            StringComparison.Ordinal);

    // The vendored WSL2 build, or null when it is not installed.
    private string? WslVariantLibraryPath()
    {
        string own = _wsl2Spec.LibraryPath(_installRoot);
        if (File.Exists(own))
        {
            return own;
        }

        return _installRoot == ExecutionProviderCatalog.DefaultInstallRoot
            ? _wsl2Spec.FindLibrary()
            : null;
    }

    // The version of the vendored WSL2 build actually in use, which predates the pin when it could not be replaced.
    private string WslBuildVersion() =>
        WslVariantLibraryPath() is { } library && !_wsl2Spec.IsCurrent(library)
            ? _wsl2Spec.VendoredVersion(library) ?? "(unknown version)"
            : _wsl2Spec.Version;

    // A build of an earlier version failing to load is most often why it was replaced: say so.
    private string StaleWsl2BuildHint(string library) =>
        IsWslVariant(library) && !_wsl2Spec.IsCurrent(library)
            ? $" The vendored WSL2 build is {_wsl2Spec.VendoredVersion(library) ?? "of unknown version"}, but {_wsl2Spec.Version} is pinned: "
                + "allow downloads or run 'linml setup --trt-rtx-wsl2' to replace it."
            : "";

    public ProviderReadyState ReadyState
    {
        get
        {
            if (UnsupportedReason() is not null)
            {
                return ProviderReadyState.Unsupported;
            }

            // Not vendored under this catalog's root. Devices from some other registration (an
            // env mapping, another root) do not make this catalog entry installed or ready.
            if (LibraryPath is null)
            {
                return ProviderReadyState.NotPresent;
            }

            return GetDevices().Count > 0 ? ProviderReadyState.Ready : ProviderReadyState.NotReady;
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
            string bundledDir = Path.GetDirectoryName(library) ?? _spec.InstallDirectory(_installRoot);
            if (SelectVariant() == Variant.Custom)
            {
                // A locally built plugin links the same runtime as the stock wheel; preload it
                // when vendored so registration does not depend on the loader path.
                if (StockRuntimeDirectory() is { } stockDir)
                {
                    _bundledLibraryHandles.AddRange(PluginInstaller.LoadBundledLibraries(stockDir, _spec.LibraryFileName));
                }

                _bundledLibraryHandles.AddRange(PluginInstaller.LoadBundledLibraries(bundledDir, _spec.LibraryFileName));
                _registrar.Register(_spec.RegistrationName, library);
                return true;
            }

            if (IsWslVariant(library))
            {
                // The WSL2 build links the TensorRT-RTX runtime that only the stock wheel
                // bundles; without it the native load fails opaquely.
                if (StockRuntimeDirectory() is not { } runtimeDir)
                {
                    failureReason = $"The WSL2 build of {Id} is vendored at {library}, but the stock {Id} wheel "
                        + "that supplies its runtime libraries is not. Run 'linml setup --trt-rtx-wsl2'.";
                    return false;
                }

                _bundledLibraryHandles.AddRange(PluginInstaller.LoadBundledLibraries(runtimeDir, _spec.LibraryFileName));
            }

            _bundledLibraryHandles.AddRange(
                PluginInstaller.LoadBundledLibraries(bundledDir, _spec.LibraryFileName));
            _registrar.Register(_spec.RegistrationName, library);
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or ArgumentException or IOException or DllNotFoundException)
        {
            failureReason = $"Registering {library} failed: {ex.Message}{StaleWsl2BuildHint(library)}";
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

        // Checked before any download: a host too old for the wheel would otherwise fetch it and then fail to load it.
        if (GlibcReason() is { } glibcReason)
        {
            return Result(ProviderEnsureStatus.NotAvailable, glibcReason);
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
                // ORT keeps one registration per EP name for the process, so a custom override set
                // after another build registered cannot take effect until restart.
                if (CustomLibraryPath() is { } custom
                    && _registrar.RegisteredPath(_spec.RegistrationName) is { } active
                    && !string.Equals(active, custom, StringComparison.Ordinal))
                {
                    return Result(
                        ProviderEnsureStatus.Failed,
                        $"{Id} is already registered from {active}; {TrtRtxCustomEpEnvVar} ({custom}) cannot replace it in this process. Restart with the override set before the first registration.");
                }

                return new ProviderEnsureResult(
                    Id, ProviderEnsureStatus.Ready, $"{Id} already registered with {existing.Count} EP device(s).", existing);
            }

            bool downloaded = false;
            Variant variant = SelectVariant();
            if (variant != Variant.Custom)
            {
                // On the WSL2 path the patched build needs the stock wheel's runtime directory and
                // must never be preceded by a stock registration: ORT cannot replace one per process.
                var needed = new List<PluginPackageSpec>();
                if (variant == Variant.Wsl2)
                {
                    if (StockRuntimeDirectory() is null)
                    {
                        needed.Add(_spec);
                    }

                    // A vendored build of another version (the pin moved on; 0.4.0-wsl2p1 needs a newer glibc than
                    // most hosts have) is replaced when downloads are allowed. Without downloads it stays in use:
                    // it is all there is, and where it loads it works.
                    if (WslVariantLibraryPath() is not { } vendored
                        || (options.AllowDownloads && !_wsl2Spec.IsCurrent(vendored)))
                    {
                        needed.Add(_wsl2Spec);
                    }
                }
                else if (LibraryPath is null)
                {
                    needed.Add(_spec);
                }

                foreach (PluginPackageSpec spec in needed)
                {
                    if (!options.AllowDownloads)
                    {
                        return Result(
                            ProviderEnsureStatus.DownloadDisallowed,
                            $"{Id} {spec.Version} is not vendored at {spec.InstallDirectory(_installRoot)} and downloads are not allowed.");
                    }

                    try
                    {
                        downloaded |= await _installer.InstallAsync(spec, _installRoot, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        return Result(ProviderEnsureStatus.Failed, $"Vendoring {Id} {spec.Version} failed: {ex.Message}");
                    }
                    catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        // HttpClient.Timeout surfaces as TaskCanceledException; caller cancellation still propagates.
                        return Result(ProviderEnsureStatus.Failed, $"Vendoring {Id} {spec.Version} timed out: {ex.Message}");
                    }
                }
            }
            else if (LibraryPath is null)
            {
                return Result(ProviderEnsureStatus.Failed, $"{TrtRtxCustomEpEnvVar} no longer points at an existing file.");
            }

            if (!TryRegister(out string? failure))
            {
                return Result(ProviderEnsureStatus.Failed, failure!);
            }

            IReadOnlyList<OrtEpDevice> devices = GetDevices();
            if (devices.Count == 0)
            {
                string detail = variant switch
                {
                    Variant.Wsl2 => $"{Id} (patched WSL2 build {WslBuildVersion()}) is registered but exposes no EP devices. Check that 'nvidia-smi' works in this distro and that /dev/dxg exists (WSL2 GPU passthrough).",
                    Variant.Custom => $"{Id} (custom build from {TrtRtxCustomEpEnvVar}) is registered but exposes no EP devices on this host.",
                    _ when Id == ProviderId.TensorrtRtx && IsWsl() && Wsl2OptedOut() =>
                        $"{Id} is registered, but in WSL2 virtual PCI mapping blocks device enumeration with the stock wheel (see docs/compatibility.md). You opted out of the patched WSL2 build: remove {Wsl2OptOutEnvVar}=0 from the environment to use it, or use bare-metal Linux.",
                    _ when Id == ProviderId.TensorrtRtx && IsWsl() =>
                        $"{Id} is registered, but no WSL2 GPU passthrough was found (/dev/dxg is missing), so the patched WSL2 build was not used and the stock wheel exposes no devices. Check that this is WSL2 with NVIDIA GPU support and that 'nvidia-smi' works.",
                    _ => $"{Id} is registered but exposes no EP devices on this host.",
                };
                return Result(ProviderEnsureStatus.NoDevices, detail);
            }

            return new ProviderEnsureResult(
                Id,
                downloaded ? ProviderEnsureStatus.Downloaded : ProviderEnsureStatus.Ready,
                variant switch
                {
                    Variant.Wsl2 => $"{Id} patched WSL2 build {WslBuildVersion()} ready with {devices.Count} EP device(s).",
                    Variant.Custom => $"{Id} custom build ({TrtRtxCustomEpEnvVar}) ready with {devices.Count} EP device(s).",
                    _ => $"{Id} {_spec.Version} ready with {devices.Count} EP device(s).",
                },
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
            return $"{Id} plugin {_spec.Version} is available for linux-{_spec.Architecture} only.";
        }

        if (GlibcReason() is { } glibcReason)
        {
            return glibcReason;
        }

        Version ort = _registrar.OrtVersion;
        return ort < _spec.MinimumOrtVersion
            ? $"{Id} plugin {_spec.Version} needs ONNX Runtime {_spec.MinimumOrtVersion} or newer; loaded ONNX Runtime is {ort}."
            : null;
    }

    private ProviderEnsureResult Result(ProviderEnsureStatus status, string detail) => new(Id, status, detail, []);

    /// <summary>The glibc this process runs on, or null when there is none (musl, or not Linux).</summary>
    internal static Version? HostGlibc { get; } = DetectGlibc();

    /// <summary>Whether a host glibc satisfies a plugin's minimum. A plugin with no minimum always does.</summary>
    internal static bool GlibcSatisfies(Version? required, Version? host) =>
        required is null || (host is not null && host >= required);

    // The wheel's glibc floor is a runtime requirement, separate from the architecture
    // check above, so an arm64 host on an older distribution is reported, not downloaded.
    private string? GlibcReason()
    {
        if (GlibcSatisfies(_spec.MinimumGlibc, HostGlibc))
        {
            return null;
        }

        string host = HostGlibc is null ? "no glibc (not a glibc system)" : $"glibc {HostGlibc}";
        return $"{Id} plugin {_spec.Version} needs glibc {_spec.MinimumGlibc} or newer; this host has {host}.";
    }

    [DllImport("libc", EntryPoint = "gnu_get_libc_version")]
    private static extern IntPtr GnuGetLibcVersion();

    private static Version? DetectGlibc()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return Version.TryParse(Marshal.PtrToStringAnsi(GnuGetLibcVersion()), out Version? version) ? version : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a plugin built for <paramref name="architecture"/> (<c>x64</c> or <c>arm64</c>) can run on this
    /// host: Linux, with the same processor architecture. Pure, so tests can check every combination.
    /// </summary>
    internal static bool SupportsHost(string architecture, bool isLinux, Architecture hostArchitecture)
    {
        Architecture? built = architecture switch
        {
            "x64" => Architecture.X64,
            "arm64" => Architecture.Arm64,
            _ => null,
        };

        return isLinux && built == hostArchitecture;
    }

    /// <summary>Whether this host is WSL2. Replaceable so tests can exercise WSL-only selection anywhere.</summary>
    internal static Func<bool> WslProbe { get; set; } = DetectWsl;

    internal static bool IsWsl() => WslProbe();

    /// <summary>Whether /dev/dxg (WSL2 GPU passthrough) exists. Replaceable for tests.</summary>
    internal static Func<bool> DxgProbe { get; set; } = () => File.Exists("/dev/dxg");

    /// <summary>Environment reader. Replaceable for tests.</summary>
    internal static Func<string, string?> EnvReader { get; set; } = Environment.GetEnvironmentVariable;

    internal const string TrtRtxCustomEpEnvVar = "TRT_RTX_CUSTOM_EP";
    internal const string Wsl2OptOutEnvVar = "LINML_TRT_RTX_WSL2";

    // WSL1 reports "microsoft" too but has no GPU, so downloading the patched build needs /dev/dxg.
    internal static bool IsWsl2() => IsWsl() && DxgProbe();

    private static bool DetectWsl()
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
