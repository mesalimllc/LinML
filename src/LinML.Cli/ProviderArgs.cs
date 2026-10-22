using LinML.Catalog;
using LinML.Contracts;
using LinML.Runtime.Onnx;

namespace LinML.Cli;

/// <summary>
/// Shared CLI parsing for provider selection and EP configuration.
/// </summary>
public static class ProviderArgs
{
    /// <summary>Environment variable with ';'-separated name=path plugin EP libraries.</summary>
    public const string EpLibrariesEnvVar = "LINML_EP_LIBRARIES";

    /// <summary>
    /// Environment variable pointing at a locally built TRT-RTX plugin .so.
    /// Overrides the vendored wheel for the trt-rtx provider; used for the
    /// WSL2 fallback build (scripts/build-trt-rtx-ep.sh). See docs/compatibility.md.
    /// </summary>
    public const string TrtRtxCustomEpEnvVar = "TRT_RTX_CUSTOM_EP";

    /// <summary>Known providers and their accepted --provider aliases.</summary>
    private static readonly (string[] Aliases, ProviderId Id)[] KnownProviders =
    [
        (["cpu", "cpuexecutionprovider"], ProviderId.Cpu),
        (["cuda", "cudaexecutionprovider"], ProviderId.Cuda),
        (["trt", "tensorrt", "tensorrtexecutionprovider"], ProviderId.Tensorrt),
        (["trt-rtx", "tensorrt-rtx", "nvtensort-rtx", "nv-tensorrt-rtx", "nvtensorrtrtxexecutionprovider", "tensorrtrtxexecutionprovider"], ProviderId.TensorrtRtx),
        (["openvino", "ov", "openvinoexecutionprovider"], ProviderId.OpenVino),
        (["qnn", "qnnexecutionprovider"], ProviderId.Qnn),
        (["migraphx", "amd", "migraphxexecutionprovider"], ProviderId.MigraphX),
    ];

    /// <summary>
    /// Parse a --provider value: short alias (cpu, cuda, trt, trt-rtx, openvino,
    /// qnn, migraphx) or a raw ORT EP name (e.g. CUDAExecutionProvider).
    /// Returns null when no provider was requested.
    /// </summary>
    public static ProviderId? ParseProvider(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var lowered = name.Trim().ToLowerInvariant();
        foreach (var (aliases, id) in KnownProviders)
        {
            if (aliases.Contains(lowered))
            {
                return id;
            }
        }

        if (name.Contains("ExecutionProvider", StringComparison.Ordinal))
        {
            return new ProviderId(name.Trim());
        }

        throw new ArgumentException(
            $"Unknown provider '{name}'. Aliases: cpu, cuda, trt, trt-rtx, openvino, qnn, migraphx " +
            "(or a raw ORT EP name like CUDAExecutionProvider).");
    }

    private static readonly List<IntPtr> BundledLibraryHandles = [];

    /// <summary>
    /// Gets the canonical short alias for a known provider ID.
    /// </summary>
    public static string GetShortName(ProviderId id)
    {
        if (id == ProviderId.Cpu) return "cpu";
        if (id == ProviderId.Cuda) return "cuda";
        if (id == ProviderId.Tensorrt) return "tensorrt";
        if (id == ProviderId.TensorrtRtx) return "trt-rtx";
        if (id == ProviderId.OpenVino) return "openvino";
        if (id == ProviderId.MigraphX) return "migraphx";
        if (id == ProviderId.Qnn) return "qnn";
        return id.Value;
    }

    /// <summary>
    /// Parse repeated --ep-library name=path arguments plus the
    /// LINML_EP_LIBRARIES environment variable (';'-separated name=path).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseEpLibraries(IEnumerable<string> args) =>
        ResolveEpLibraries(args, requestedProvider: null);

    /// <summary>
    /// Parse repeated --ep-library name=path arguments and LINML_EP_LIBRARIES,
    /// and automatically resolve and register vendored libraries from the catalog
    /// cache (~/.cache/linml/ep-plugins, ~/.local/linml-deps, etc.) if --ep-library
    /// was omitted for the requested provider.
    /// </summary>
    /// <param name="args">Repeated --ep-library name=path arguments.</param>
    /// <param name="requestedProvider">The provider whose plugin library is resolved, if any.</param>
    /// <param name="customRoot">Catalog install root to search before the defaults.</param>
    /// <param name="skipMalformed">Ignore entries not of the form name=path instead of throwing.</param>
    public static IReadOnlyDictionary<string, string> ResolveEpLibraries(
        IEnumerable<string> args,
        ProviderId? requestedProvider = null,
        string? customRoot = null,
        bool skipMalformed = false)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        var env = Environment.GetEnvironmentVariable(EpLibrariesEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var pair in env.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                AddPair(result, pair, "environment variable " + EpLibrariesEnvVar, skipMalformed);
            }
        }

        // Keys named explicitly on the command line. These outrank both the
        // inherited environment mapping and TRT_RTX_CUSTOM_EP.
        var explicitArgKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            AddPair(result, arg, "--ep-library", skipMalformed);
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                explicitArgKeys.Add(arg[..eq].Trim());
            }
        }

        if (requestedProvider is not null)
        {
            var spec = KnownPlugins.All.FirstOrDefault(p => p.Id == requestedProvider);
            if (spec is not null)
            {
                var shortName = GetShortName(spec.Id);
                bool MatchesProvider(string key) =>
                    string.Equals(key, spec.RegistrationName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, spec.Id.Value, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, shortName, StringComparison.OrdinalIgnoreCase);

                bool explicitOnCommandLine = explicitArgKeys.Any(MatchesProvider);
                bool alreadySpecified = result.Keys.Any(MatchesProvider);

                string? libraryPath = null;

                // TRT_RTX_CUSTOM_EP points at a locally built plugin .so. It is the
                // documented override for trt-rtx, so it outranks a mapping inherited
                // from the environment (env-gpu-deps.sh); an explicit --ep-library
                // still wins over it.
                if (spec.Id == ProviderId.TensorrtRtx && !explicitOnCommandLine)
                {
                    var custom = Environment.GetEnvironmentVariable(TrtRtxCustomEpEnvVar);
                    if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom))
                    {
                        libraryPath = custom;
                    }
                }

                // The opted-in WSL2 build outranks the stock wheel on WSL2 hosts: both when
                // nothing is mapped and when the inherited mapping (a shell that sourced
                // env-gpu-deps.sh before the patch was installed) is just the stock wheel.
                // An explicit --ep-library, TRT_RTX_CUSTOM_EP, or any other inherited path
                // stays authoritative.
                if (libraryPath is null && !explicitOnCommandLine && spec.Id == ProviderId.TensorrtRtx
                    && (!alreadySpecified || result.Where(kv => MatchesProvider(kv.Key)).Any(kv => IsStockWheelPath(kv.Value, spec))))
                {
                    libraryPath = ResolveWslVariant(spec, customRoot);
                }

                if (libraryPath is null && !alreadySpecified)
                {
                    libraryPath = spec.FindLibrary(customRoot);
                }

                if (libraryPath is not null)
                {
                    // Drop any inherited/aliased mapping for this provider so it is
                    // registered once, under the canonical name.
                    foreach (var key in result.Keys.Where(MatchesProvider).ToList())
                    {
                        result.Remove(key);
                    }

                    var pluginDir = Path.GetDirectoryName(libraryPath);
                    if (!string.IsNullOrEmpty(pluginDir))
                    {
                        BundledLibraryHandles.AddRange(
                            PluginInstaller.LoadBundledLibraries(pluginDir, spec.LibraryFileName));
                    }

                    result[spec.RegistrationName] = libraryPath;
                }
            }
        }

        return result;
    }

    private static bool IsStockWheelPath(string path, PluginPackageSpec spec) =>
        string.Equals(Path.GetFileName(path), spec.LibraryFileName, StringComparison.Ordinal)
        && string.Equals(
            Path.GetFileName(Path.GetDirectoryName(path)),
            Path.GetFileName(spec.InstallSubdirectory),
            StringComparison.Ordinal);

    /// <summary>
    /// The vendored WSL2 build of <paramref name="spec"/> when this host is WSL2, it is
    /// installed, and the stock wheel's runtime libraries it links against are present
    /// (they are loaded here, ahead of the plugin); otherwise null.
    /// </summary>
    private static string? ResolveWslVariant(PluginPackageSpec spec, string? customRoot)
    {
        string? variant = KnownPlugins.WslVariantLibrary(spec, customRoot);
        if (variant is null || KnownPlugins.WslVariantRuntimeDirectory(customRoot) is not { } runtimeDir)
        {
            return null;
        }

        BundledLibraryHandles.AddRange(PluginInstaller.LoadBundledLibraries(runtimeDir, spec.LibraryFileName));
        return variant;
    }

    /// <summary>Parse repeated --ep-option key=value arguments into a dictionary.</summary>
    public static IReadOnlyDictionary<string, string> ParseEpOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in args)
        {
            var idx = arg.IndexOf('=');
            if (idx <= 0)
            {
                throw new ArgumentException(
                    $"Invalid --ep-option '{arg}'. Expected key=value or provider:key=value.");
            }
            result[arg[..idx]] = arg[(idx + 1)..];
        }
        return result;
    }

    /// <summary>
    /// Select the EP options that apply to a specific provider.
    /// Unprefixed keys (key=value) are global — they apply to every provider.
    /// Prefixed keys (provider:key=value, where provider is an alias or the raw
    /// EP name, case-insensitive) apply only to that provider. This keeps
    /// provider-specific options from leaking into fallback attempts of other
    /// providers (e.g. a trt_* option must not reach the CUDA append).
    /// </summary>
    public static IReadOnlyDictionary<string, string> OptionsForProvider(
        IReadOnlyDictionary<string, string> options,
        ProviderId provider)
    {
        var aliases = KnownProviders
            .FirstOrDefault(p => p.Id == provider).Aliases
            ?? [];
        var epNameLower = provider.Value.ToLowerInvariant();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in options)
        {
            var colon = key.IndexOf(':');
            if (colon < 0)
            {
                result[key] = value; // global
                continue;
            }

            var prefix = key[..colon].ToLowerInvariant();
            if (aliases.Contains(prefix) || prefix == epNameLower)
            {
                result[key[(colon + 1)..]] = value;
            }
        }
        return result;
    }

    /// <summary>
    /// Register plugin EP libraries with the ORT environment.
    /// Fails fast with a clear error when a library path is wrong.
    /// </summary>
    public static void EnsureEpLibrariesRegistered(IReadOnlyDictionary<string, string> epLibraries)
    {
        try
        {
            PluginEpRegistry.EnsureRegistered(epLibraries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ArgumentException(
                $"Failed to register EP plugin library: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Register plugin EP libraries one at a time, isolating failures. A library named on
    /// the command line (<paramref name="epLibraryArgs"/>) or mapped for
    /// <paramref name="requiredProvider"/> still fails fast, as
    /// <see cref="EnsureEpLibrariesRegistered"/> does; any other library that fails to
    /// register (typically one inherited from LINML_EP_LIBRARIES whose runtime is missing
    /// on this host) is reported to <paramref name="warnings"/> and left out of the result,
    /// so it cannot break commands that do not use it.
    /// </summary>
    /// <returns>The libraries that registered.</returns>
    public static IReadOnlyDictionary<string, string> RegisterEpLibraries(
        IReadOnlyDictionary<string, string> epLibraries,
        IEnumerable<string> epLibraryArgs,
        ProviderId? requiredProvider = null,
        TextWriter? warnings = null)
    {
        var explicitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in epLibraryArgs)
        {
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                explicitKeys.Add(arg[..eq].Trim());
            }
        }

        var requiredSpec = requiredProvider is null
            ? null
            : LinML.Catalog.KnownPlugins.All.FirstOrDefault(p => p.Id == requiredProvider);

        var registered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, path) in epLibraries)
        {
            bool required = explicitKeys.Contains(name)
                || (requiredSpec is not null && IsMappingFor(name, requiredSpec))
                || (requiredProvider is not null && string.Equals(name, requiredProvider.Value, StringComparison.OrdinalIgnoreCase));
            if (required)
            {
                EnsureEpLibrariesRegistered(new Dictionary<string, string> { [name] = path });
                registered[name] = path;
                continue;
            }

            try
            {
                PluginEpRegistry.EnsureRegistered(new Dictionary<string, string> { [name] = path });
                registered[name] = path;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings?.WriteLine($"Warning: skipping EP plugin library '{name}' ({path}): {ex.Message}");
            }
        }
        return registered;
    }

    /// <summary>
    /// Why <paramref name="provider"/> cannot run here and what to do about it, as a sentence
    /// to append to an error: the host lacks its hardware or architecture (no install hint, as
    /// installing it would not help), or its plugin library is not installed. Null otherwise.
    /// </summary>
    public static string? ProviderUnavailableHint(ProviderId? provider, string? root = null)
    {
        var spec = LinML.Catalog.KnownPlugins.All.FirstOrDefault(p => p.Id == provider);
        if (spec is null)
        {
            return null;
        }

        string? gap = Commands.SetupCommand.ArchitectureNote(
                spec, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant())
            ?? Commands.SetupCommand.MigraphXHardwareNote(spec.Id, Commands.SetupCommand.ProbeAmdRocm());
        if (gap is not null)
        {
            return $"{GetShortName(spec.Id)} is not applicable on this host: {gap}.";
        }

        return spec.FindLibrary(root) is null
            ? $"Run 'linml catalog install {GetShortName(spec.Id)}' to vendor missing libraries."
            : null;
    }

    /// <summary>
    /// Registers, in this process, every catalog plugin that resolves on this host, so that
    /// discovery, readiness and catalog status all see the same EP devices. A plugin that
    /// does not resolve or fails to register is left for the caller's own checks to report.
    /// </summary>
    public static void RegisterCatalogPlugins(IEnumerable<string> epLibraryArgs, string? root)
    {
        var args = epLibraryArgs.ToList();
        foreach (var spec in LinML.Catalog.KnownPlugins.All)
        {
            IReadOnlyDictionary<string, string> resolved;
            try
            {
                resolved = ResolveEpLibraries(args, spec.Id, root, skipMalformed: true);
            }
            catch
            {
                continue; // this plugin's own mapping cannot be resolved; its readiness report says so
            }

            // Only this plugin's own mapping. An unrelated bad mapping (a missing file for another
            // plugin) must not stop this one from registering.
            foreach (var (name, path) in resolved.Where(kv => IsMappingFor(kv.Key, spec)))
            {
                try
                {
                    EnsureEpLibrariesRegistered(new Dictionary<string, string> { [name] = path });
                }
                catch
                {
                    // The failure is recorded by PluginLibraryRegistry, so readiness reports can say why.
                }
            }
        }
    }

    private static bool IsMappingFor(string key, LinML.Catalog.PluginPackageSpec spec)
    {
        string shortName = GetShortName(spec.Id);
        return string.Equals(key, spec.RegistrationName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, spec.Id.Value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, shortName, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPair(
        Dictionary<string, string> result, string pair, string source, bool skipMalformed = false)
    {
        var idx = pair.IndexOf('=');
        if (idx <= 0)
        {
            if (skipMalformed)
            {
                return;
            }

            throw new ArgumentException(
                $"Invalid EP library '{pair}' from {source}. Expected name=path.");
        }
        result[pair[..idx].Trim()] = pair[(idx + 1)..].Trim();
    }
}
