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
}

/// <summary>
/// Registers plugin libraries with the process-wide <see cref="OrtEnv"/>.
/// Registration is global to the process, so the bookkeeping is static.
/// </summary>
internal sealed class OrtRegistrar : IOrtRegistrar
{
    public static readonly OrtRegistrar Instance = new();

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Registered = new(StringComparer.Ordinal);

    private OrtRegistrar()
    {
    }

    public Version OrtVersion
    {
        get
        {
            string raw = OrtEnv.Instance().GetVersionString();
            // Tolerate suffixes such as "1.31.0-dev".
            string numeric = raw.Split('-', '+')[0];
            return Version.TryParse(numeric, out Version? version) ? version : new Version(0, 0);
        }
    }

    public bool IsRegistered(string registrationName)
    {
        lock (Gate)
        {
            return Registered.ContainsKey(registrationName);
        }
    }

    public void Register(string registrationName, string libraryPath)
    {
        lock (Gate)
        {
            if (Registered.TryGetValue(registrationName, out string? existing))
            {
                if (!string.Equals(existing, libraryPath, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"'{registrationName}' is already registered with a different library: {existing}");
                }

                return;
            }

            OrtEnv.Instance().RegisterExecutionProviderLibrary(registrationName, libraryPath);
            Registered[registrationName] = libraryPath;
        }
    }

    public IReadOnlyList<OrtEpDevice> GetDevices(string epName) =>
        OrtEnv.Instance().GetEpDevices()
            .Where(d => string.Equals(d.EpName, epName, StringComparison.Ordinal))
            .ToList();
}
