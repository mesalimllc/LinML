using LinML.Catalog;
using LinML.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Registers plugin EP libraries supplied by name→path (the CLI's
/// <c>--ep-library</c> / <c>LINML_EP_LIBRARIES</c>). A thin forwarder to the
/// process-wide <see cref="PluginLibraryRegistry"/> in LinML.Catalog, so the
/// CLI, readiness probes and the catalog share one registration record.
/// </summary>
public static class PluginEpRegistry
{
    /// <summary>
    /// Register plugin EP libraries with the ORT environment.
    /// </summary>
    /// <param name="libraries">Mapping of registration name to library path.</param>
    /// <exception cref="FileNotFoundException">A library does not exist.</exception>
    /// <exception cref="ArgumentException">A name is already registered with a different path.</exception>
    /// <exception cref="System.Exception">A library failed to load.</exception>
    public static void EnsureRegistered(IReadOnlyDictionary<string, string>? libraries)
    {
        if (libraries is null || libraries.Count == 0)
        {
            return;
        }

        foreach (var (name, path) in libraries)
        {
            PluginLibraryRegistry.Register(name, path);
        }
    }

    /// <summary>Names of plugin EP libraries registered in this process.</summary>
    public static IReadOnlyCollection<string> RegisteredNames => PluginLibraryRegistry.RegisteredNames;
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
                var devices = GetEpDevices(provider);
                if (devices.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Plugin EP '{provider}' is registered but exposes no EP devices. " +
                        "Check GPU presence and driver (RTX Ampere+ required).");
                }
                AppendDevices(sessionOptions, devices, options);
                break;
            }

            default:
            {
                // An EP registered as a plugin library (e.g. OpenVINO from the
                // onnxruntime-openvino wheel) is unknown to the name-based API
                // unless compiled into this ORT build, so append its devices
                // via the V2 API instead.
                var devices = GetEpDevices(provider);
                if (devices.Count > 0)
                {
                    AppendDevices(sessionOptions, devices, options);
                    break;
                }

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

    private static IReadOnlyList<OrtEpDevice> GetEpDevices(ProviderId provider) =>
        PluginLibraryRegistry.GetDevices(provider.Value);

    private static void AppendDevices(
        SessionOptions sessionOptions,
        IReadOnlyList<OrtEpDevice> devices,
        IReadOnlyDictionary<string, string>? options) =>
        sessionOptions.AppendEpDevices(devices, options);
}
