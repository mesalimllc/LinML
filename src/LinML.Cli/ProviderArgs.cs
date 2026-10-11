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

    /// <summary>Known providers and their accepted --provider aliases.</summary>
    private static readonly (string[] Aliases, ProviderId Id)[] KnownProviders =
    [
        (["cpu"], ProviderId.Cpu),
        (["cuda"], ProviderId.Cuda),
        (["trt", "tensorrt"], ProviderId.Tensorrt),
        (["trt-rtx", "tensorrt-rtx", "nvtensort-rtx", "nv-tensorrt-rtx"], ProviderId.TensorrtRtx),
        (["openvino", "ov"], ProviderId.OpenVino),
        (["qnn"], ProviderId.Qnn),
        (["migraphx", "amd"], ProviderId.MigraphX),
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

    /// <summary>
    /// Parse repeated --ep-library name=path arguments plus the
    /// LINML_EP_LIBRARIES environment variable (';'-separated name=path).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseEpLibraries(IEnumerable<string> args)
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

        foreach (var arg in args)
        {
            AddPair(result, arg, "--ep-library");
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
