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
    public static IReadOnlyDictionary<string, string> ResolveEpLibraries(
        IEnumerable<string> args,
        ProviderId? requestedProvider = null,
        string? customRoot = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        var env = Environment.GetEnvironmentVariable(EpLibrariesEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var pair in env.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                AddPair(result, pair, "environment variable " + EpLibrariesEnvVar);
            }
        }

        // Keys named explicitly on the command line. These outrank both the
        // inherited environment mapping and TRT_RTX_CUSTOM_EP.
        var explicitArgKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            AddPair(result, arg, "--ep-library");
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

    private static void AddPair(
        Dictionary<string, string> result, string pair, string source)
    {
        var idx = pair.IndexOf('=');
        if (idx <= 0)
        {
            throw new ArgumentException(
                $"Invalid EP library '{pair}' from {source}. Expected name=path.");
        }
        result[pair[..idx].Trim()] = pair[(idx + 1)..].Trim();
    }
}
