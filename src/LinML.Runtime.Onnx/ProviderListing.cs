using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Whether ORT lists a provider, counting both built-in providers and plugin EP devices,
/// and why it does not when it is missing. Shared by discovery-level readiness and smoke.
/// </summary>
public static class ProviderListing
{
    /// <summary>True when ORT lists the provider as built-in or exposes an EP device for it.</summary>
    public static bool IsListed(ProviderId provider) =>
        OrtEnv.Instance().GetAvailableProviders().Contains(provider.Value)
        || HasEpDevices(provider);

    /// <summary>True when ORT exposes at least one EP device for the provider (plugin EPs).</summary>
    public static bool HasEpDevices(ProviderId provider) =>
        OrtEnv.Instance().GetEpDevices().Any(d => d.EpName == provider.Value);

    /// <summary>
    /// The reason a provider is not listed, worded for the state it is in: built-in, loaded but
    /// without devices, failed to load (with ORT's message), or not loaded at all.
    /// </summary>
    public static string NotListedReason(ProviderId provider)
    {
        var plugin = KnownPlugins.All.FirstOrDefault(p => p.Id == provider);
        if (plugin is null)
        {
            return $"Provider '{provider}' is not listed by the ORT runtime (not built into this ONNX Runtime, or no device is available).";
        }

        // Matched by library file name: a library can be registered under an alias key
        // (e.g. --ep-library trt-rtx=...), not only under the plugin's registration name.
        string file = plugin.LibraryFileName;
        if (PluginLibraryRegistry.RegisteredLibraryFileNames.Contains(file))
        {
            return $"Provider '{provider}' is registered with ORT but exposes no EP devices on this host.";
        }

        if (PluginLibraryRegistry.RegistrationFailure(file) is { } failure)
        {
            return $"Provider '{provider}' is installed but failed to register: {failure}";
        }

        return $"Provider '{provider}' is not loaded into this process. If the plugin is not installed, run 'linml catalog install {ShortName(provider)}' to vendor it.";
    }

    // Matches the CLI's short names for these providers (ProviderArgs.GetShortName).
    private static string ShortName(ProviderId provider) => provider.Value switch
    {
        "NvTensorRTRTXExecutionProvider" => "trt-rtx",
        "OpenVINOExecutionProvider" => "openvino",
        "MIGraphXExecutionProvider" => "migraphx",
        _ => provider.Value,
    };
}
