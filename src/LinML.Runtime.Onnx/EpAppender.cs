using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Process-wide registry for ORT plugin execution provider libraries
/// (e.g. the standalone TensorRT-RTX EP). Registration is global to the
/// OrtEnv and idempotent: registering the same name twice is a no-op.
/// </summary>
public static class PluginEpRegistry
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);

    /// <summary>
    /// Register plugin EP libraries with the ORT environment.
    /// </summary>
    /// <param name="libraries">Mapping of registration name to library path.</param>
    /// <exception cref="System.Exception">A library failed to load.</exception>
    public static void EnsureRegistered(IReadOnlyDictionary<string, string>? libraries)
    {
        if (libraries is null || libraries.Count == 0)
        {
            return;
        }

        lock (Gate)
        {
            foreach (var (name, path) in libraries)
            {
                if (Registered.Contains(name))
                {
                    continue;
                }
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"EP plugin library not found for '{name}': {path}", path);
                }

                OrtEnv.Instance().RegisterExecutionProviderLibrary(name, path);
                Registered.Add(name);
            }
        }
    }

    /// <summary>Names of plugin EP libraries registered in this process.</summary>
    public static IReadOnlyCollection<string> RegisteredNames
    {
        get { lock (Gate) { return Registered.ToArray(); } }
    }
}

/// <summary>
/// Appends execution providers to <see cref="SessionOptions"/>. Shared by the
/// session factory and the readiness probe so Load/Append behavior is identical
/// in both paths.
/// </summary>
internal static class EpAppender
{
    public static void Append(
        SessionOptions sessionOptions,
        ProviderId provider,
        IReadOnlyDictionary<string, string>? options,
        IReadOnlyDictionary<string, string>? epLibraries)
    {
        ArgumentNullException.ThrowIfNull(sessionOptions);
        ArgumentNullException.ThrowIfNull(provider);

        // Plugin EPs (registered via PluginEpRegistry) use the V2 device-based API.
        PluginEpRegistry.EnsureRegistered(epLibraries);

        switch (provider.Value)
        {
            case "CPUExecutionProvider":
                // CPU is the default provider; no explicit append needed.
                break;

            case "CUDAExecutionProvider":
            {
                using var cudaOptions = new OrtCUDAProviderOptions();
                if (options is not null)
                {
                    cudaOptions.UpdateOptions(new Dictionary<string, string>(options));
                }
                sessionOptions.AppendExecutionProvider_CUDA(cudaOptions);
                break;
            }

            case "TensorrtExecutionProvider":
            {
                using var trtOptions = new OrtTensorRTProviderOptions();
                if (options is not null)
                {
                    trtOptions.UpdateOptions(new Dictionary<string, string>(options));
                }
                sessionOptions.AppendExecutionProvider_Tensorrt(trtOptions);
                break;
            }

            case "NvTensorRTRTXExecutionProvider":
            {
                // Plugin EP: enumerate EP devices and append via the V2 API.
                var devices = OrtEnv.Instance().GetEpDevices()
                    .Where(d => d.EpName == provider.Value)
                    .ToList();
                if (devices.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Plugin EP '{provider}' is registered but exposes no EP devices. " +
                        "Check GPU presence and driver (RTX Ampere+ required).");
                }
                sessionOptions.AppendExecutionProvider(
                    OrtEnv.Instance(), devices,
                    options is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(options));
                break;
            }

            default:
            {
                // Generic path: covers MIGraphX, OpenVINO, QNN, and any other EP
                // the runtime lists. Provider options pass through verbatim.
                sessionOptions.AppendExecutionProvider(
                    provider.Value,
                    options is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(options));
                break;
            }
        }
    }
}
