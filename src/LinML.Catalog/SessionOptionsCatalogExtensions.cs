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

        if (provider.Source == ProviderSource.BuiltIn)
        {
            if (provider.Id == LinML.Contracts.ProviderId.Cpu)
            {
                if (preferredDeviceTypes is not null && preferredDeviceTypes.Count > 0 && !preferredDeviceTypes.Contains(OrtHardwareDeviceType.CPU))
                {
                    return new ProviderAppendResult(false, provider.Id, $"{provider.Id} exposes no device of the preferred types.");
                }

                // CPU is default in ONNX Runtime sessions.
                return new ProviderAppendResult(true, provider.Id);
            }

            try
            {
                IReadOnlyList<OrtEpDevice> allDevices = provider.GetDevices();
                if (allDevices.Count > 0)
                {
                    IReadOnlyList<OrtEpDevice> matched = SelectDevices(allDevices, preferredDeviceTypes);
                    if (matched.Count == 0)
                    {
                        return new ProviderAppendResult(false, provider.Id, $"{provider.Id} exposes no device of the preferred types.");
                    }

                    options.AppendEpDevices(matched, providerOptions);
                    return new ProviderAppendResult(true, provider.Id);
                }

                // Built-in GPU provider exposing no V2 EP devices: verify device preference allows GPU.
                if (preferredDeviceTypes is not null && preferredDeviceTypes.Count > 0 && !preferredDeviceTypes.Contains(OrtHardwareDeviceType.GPU))
                {
                    return new ProviderAppendResult(false, provider.Id, $"{provider.Id} exposes no device of the preferred types.");
                }

                var optDict = providerOptions is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(providerOptions);

                if (provider.Id == LinML.Contracts.ProviderId.Cuda)
                {
                    using var cudaOptions = new OrtCUDAProviderOptions();
                    cudaOptions.UpdateOptions(optDict);
                    options.AppendExecutionProvider_CUDA(cudaOptions);
                }
                else if (provider.Id == LinML.Contracts.ProviderId.Tensorrt)
                {
                    using var tensorrtOptions = new OrtTensorRTProviderOptions();
                    tensorrtOptions.UpdateOptions(optDict);
                    options.AppendExecutionProvider_Tensorrt(tensorrtOptions);
                }
                else
                {
                    options.AppendExecutionProvider(provider.Id.Value, optDict);
                }

                return new ProviderAppendResult(true, provider.Id);
            }
            catch (OnnxRuntimeException ex)
            {
                return new ProviderAppendResult(false, provider.Id, $"Appending {provider.Id} failed: {ex.Message}");
            }
        }

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
            options.AppendEpDevices(devices, providerOptions);
            return new ProviderAppendResult(true, provider.Id);
        }
        catch (OnnxRuntimeException ex)
        {
            return new ProviderAppendResult(false, provider.Id, $"Appending {provider.Id} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends EP devices through the device-based ORT API. All devices must
    /// come from the same EP factory (e.g. one
    /// <see cref="PluginLibraryRegistry.GetDevices"/> result). Unlike
    /// <see cref="AppendFromCatalog"/>, ORT failures propagate.
    /// </summary>
    /// <exception cref="OnnxRuntimeException">ORT rejected the append.</exception>
    public static void AppendEpDevices(
        this SessionOptions options,
        IReadOnlyList<OrtEpDevice> devices,
        IReadOnlyDictionary<string, string>? providerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(devices);

        options.AppendExecutionProvider(
            OrtEnv.Instance(),
            devices,
            providerOptions is null ? new Dictionary<string, string>() : new Dictionary<string, string>(providerOptions));
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
