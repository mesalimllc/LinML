using Microsoft.ML.OnnxRuntime;

namespace LinML.Catalog;

/// <summary>
/// The process-wide record of plugin EP libraries registered with
/// <see cref="OrtEnv"/>. ORT registration is global to the process, so every
/// LinML component (the catalog, the CLI's <c>--ep-library</c> path, readiness
/// probes) registers through this one registry: a name registers once, a
/// repeat with the same path is a no-op, and a different path is an error.
/// </summary>
public static class PluginLibraryRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Registered = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="libraryPath"/> with ORT under
    /// <paramref name="registrationName"/>, unless it already is.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// The library does not exist. Checked on every call, even for a name
    /// already registered, so a stale path is reported clearly instead of as
    /// an ORT loader error.
    /// </exception>
    /// <exception cref="ArgumentException">The name is already registered with a different path.</exception>
    /// <exception cref="OnnxRuntimeException">ORT could not load the library.</exception>
    public static void Register(string registrationName, string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        if (!File.Exists(libraryPath))
        {
            throw new FileNotFoundException(
                $"EP plugin library not found for '{registrationName}': {libraryPath}", libraryPath);
        }

        lock (Gate)
        {
            if (Registered.TryGetValue(registrationName, out string? existing))
            {
                if (!string.Equals(existing, libraryPath, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"EP plugin '{registrationName}' is already registered with a different path: {existing}");
                }

                return;
            }

            OrtEnv.Instance().RegisterExecutionProviderLibrary(registrationName, libraryPath);
            Registered[registrationName] = libraryPath;
        }
    }

    /// <summary>Whether <paramref name="registrationName"/> was registered through this registry.</summary>
    public static bool IsRegistered(string registrationName)
    {
        lock (Gate)
        {
            return Registered.ContainsKey(registrationName);
        }
    }

    /// <summary>Names registered through this registry in this process.</summary>
    public static IReadOnlyCollection<string> RegisteredNames
    {
        get
        {
            lock (Gate)
            {
                return Registered.Keys.ToArray();
            }
        }
    }

    /// <summary>
    /// EP devices ORT exposes for <paramref name="epName"/>, from whichever
    /// registration (or built-in EP) made them.
    /// </summary>
    public static IReadOnlyList<OrtEpDevice> GetDevices(string epName) =>
        OrtEnv.Instance().GetEpDevices()
            .Where(d => string.Equals(d.EpName, epName, StringComparison.Ordinal))
            .ToList();

    /// <summary>The loaded ONNX Runtime version; 0.0 if it cannot be parsed.</summary>
    public static Version OrtVersion
    {
        get
        {
            string raw = OrtEnv.Instance().GetVersionString();
            // Tolerate suffixes such as "1.31.0-dev".
            string numeric = raw.Split('-', '+')[0];
            return Version.TryParse(numeric, out Version? version) ? version : new Version(0, 0);
        }
    }
}
