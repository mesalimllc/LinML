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

    /// <summary>The reason a provider is not listed, worded for the state it is in.</summary>
    public static string NotListedReason(ProviderId provider)
    {
        var plugin = KnownPlugins.All.FirstOrDefault(p => p.Id == provider);
        if (plugin is null)
        {
            return $"Provider '{provider}' is not listed by the ORT runtime (not built into this ONNX Runtime, or no device is available).";
        }

        // The registry is keyed by the plugin's registration name, which need not equal the EP name
        // (OpenVINO registers as "linml.openvino"). Registered means the library was loaded here.
        return PluginEpRegistry.RegisteredNames.Contains(plugin.RegistrationName)
            ? $"Provider '{provider}' is registered with ORT but exposes no EP devices on this host."
            : $"Provider '{provider}' is not registered: its plugin library is not vendored or not loaded. Run 'linml catalog install' to vendor it.";
    }
}
