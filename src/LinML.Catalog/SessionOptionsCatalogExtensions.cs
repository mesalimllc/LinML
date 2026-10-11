using Microsoft.ML.OnnxRuntime;

namespace LinML.Catalog;

/// <summary>Appends catalog providers to <see cref="SessionOptions"/>.</summary>
public static class SessionOptionsCatalogExtensions
{
    /// <summary>
    /// Appends <paramref name="provider"/>'s EP devices through the device-based
    /// ORT API, which plugin EPs require (the by-name API only knows EPs compiled
    /// into the loaded ORT). Call <see cref="IExecutionProvider.EnsureReadyAsync"/>
    /// first. Never throws for an unavailable provider; returns why instead.
    /// </summary>
    /// <param name="options">Session options to append to.</param>
    /// <param name="provider">A catalog provider.</param>
    /// <param name="preferredDeviceTypes">
    /// Device types to try in order (e.g. NPU, GPU, CPU). The first type with a
    /// matching device wins. Null appends every device the provider exposes.
    /// </param>
    /// <param name="providerOptions">EP-specific options, passed through verbatim.</param>
    public static ProviderAppendResult AppendFromCatalog(
        this SessionOptions options,
        IExecutionProvider provider,
        IReadOnlyList<OrtHardwareDeviceType>? preferredDeviceTypes = null,
        IReadOnlyDictionary<string, string>? providerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);

        IReadOnlyList<OrtEpDevice> devices = SelectDevices(provider.GetDevices(), preferredDeviceTypes);
        if (devices.Count == 0)
        {
            string reason = provider.ReadyState == ProviderReadyState.Ready
                ? $"{provider.Id} exposes no device of the preferred types."
                : $"{provider.Id} is {provider.ReadyState}; call EnsureReadyAsync first.";
            return new ProviderAppendResult(false, provider.Id, reason);
        }

        try
        {
            options.AppendExecutionProvider(
                OrtEnv.Instance(),
                devices,
                providerOptions is null ? new Dictionary<string, string>() : new Dictionary<string, string>(providerOptions));
            return new ProviderAppendResult(true, provider.Id);
        }
        catch (OnnxRuntimeException ex)
        {
            return new ProviderAppendResult(false, provider.Id, $"Appending {provider.Id} failed: {ex.Message}");
        }
    }

    private static List<OrtEpDevice> SelectDevices(
        IReadOnlyList<OrtEpDevice> devices,
        IReadOnlyList<OrtHardwareDeviceType>? preferredDeviceTypes)
    {
        if (preferredDeviceTypes is null)
        {
            return devices.ToList();
        }

        foreach (OrtHardwareDeviceType type in preferredDeviceTypes)
        {
            List<OrtEpDevice> match = devices.Where(d => d.HardwareDevice.Type == type).ToList();
            if (match.Count > 0)
            {
                return match;
            }
        }

        return [];
    }
}
