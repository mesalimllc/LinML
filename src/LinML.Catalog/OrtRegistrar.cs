using Microsoft.ML.OnnxRuntime;

namespace LinML.Catalog;

/// <summary>The ORT calls the catalog makes; a seam so tests can run without native plugins.</summary>
internal interface IOrtRegistrar
{
    Version OrtVersion { get; }

    bool IsRegistered(string registrationName);

    /// <exception cref="OnnxRuntimeException">ORT could not load the library.</exception>
    void Register(string registrationName, string libraryPath);

    IReadOnlyList<OrtEpDevice> GetDevices(string epName);

    IReadOnlyCollection<string> AvailableProviders { get; }
}

/// <summary>The real <see cref="IOrtRegistrar"/>: the process-wide <see cref="PluginLibraryRegistry"/>.</summary>
internal sealed class OrtRegistrar : IOrtRegistrar
{
    public static readonly OrtRegistrar Instance = new();

    private OrtRegistrar()
    {
    }

    public Version OrtVersion => PluginLibraryRegistry.OrtVersion;

    public bool IsRegistered(string registrationName) => PluginLibraryRegistry.IsRegistered(registrationName);

    public void Register(string registrationName, string libraryPath) =>
        PluginLibraryRegistry.Register(registrationName, libraryPath);

    public IReadOnlyList<OrtEpDevice> GetDevices(string epName) => PluginLibraryRegistry.GetDevices(epName);

    public IReadOnlyCollection<string> AvailableProviders => OrtEnv.Instance().GetAvailableProviders();
}
