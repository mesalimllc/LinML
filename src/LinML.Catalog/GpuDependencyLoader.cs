using System.Runtime.InteropServices;

namespace LinML.Catalog;

/// <summary>
/// Makes vendored GPU runtime libraries (cuDNN, TensorRT) visible to ONNX Runtime
/// without requiring <c>LD_LIBRARY_PATH</c>. glibc matches an already-loaded
/// library by SONAME, so loading <c>libnvinfer.so.10</c> by absolute path before
/// ORT opens <c>libonnxruntime_providers_tensorrt.so</c> satisfies its
/// <c>DT_NEEDED</c> entry. A library the loader can already resolve by name (the
/// caller's <c>LD_LIBRARY_PATH</c> or a system install) is left alone.
/// </summary>
/// <remarks>
/// <see cref="ExecutionProviderCatalog"/> and LinML's session factory call this
/// automatically; an application that opens TensorRT or CUDA sessions through ONNX
/// Runtime directly can call <see cref="PreloadVendored"/> once at startup instead of
/// exporting <c>LD_LIBRARY_PATH</c>.
/// </remarks>
public static class GpuDependencyLoader
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Loaded = new(StringComparer.Ordinal);
    // Handles stay referenced for the life of the process; the libraries are never unloaded.
    private static readonly List<IntPtr> Handles = [];
    private static bool _defaultRootDone;

    /// <summary>Vendored dependency roots in priority order: <c>LINML_DEPS</c>, then the default root.</summary>
    internal static IReadOnlyList<string> CandidateRoots(string? customRoot = null)
    {
        List<string> roots = [];
        if (!string.IsNullOrWhiteSpace(customRoot))
        {
            roots.Add(customRoot);
        }

        string? env = Environment.GetEnvironmentVariable("LINML_DEPS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            roots.Add(env);
        }

        roots.Add(ExecutionProviderCatalog.DefaultInstallRoot);
        return roots;
    }

    /// <summary>
    /// Loads each installed dependency's runtime libraries. Never throws: a library
    /// that fails to load is simply left for ORT to report when it is needed.
    /// </summary>
    /// <param name="installRoot">
    /// Vendored dependency root to search first; <c>null</c> uses <c>LINML_DEPS</c>, then
    /// <c>~/.local/linml-deps</c>. The default-root form is a no-op after the first call.
    /// </param>
    /// <returns>Absolute paths of the libraries loaded from a vendored directory by this call.</returns>
    public static IReadOnlyList<string> PreloadVendored(string? installRoot = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            return [];
        }

        List<string> loadedNow = [];
        lock (Gate)
        {
            if (installRoot is null && _defaultRootDone)
            {
                return loadedNow;
            }

            foreach (GpuDependencySpec spec in KnownGpuDependencies.All)
            {
                string? root = CandidateRoots(installRoot).FirstOrDefault(spec.IsInstalled);
                if (root is null)
                {
                    continue;
                }

                string directory = spec.LibraryDirectory(root);
                foreach (string soname in spec.PreloadLibraries)
                {
                    if (Loaded.Contains(soname))
                    {
                        continue;
                    }

                    // Already resolvable by name (LD_LIBRARY_PATH / system): respect it.
                    if (NativeLibrary.TryLoad(soname, out IntPtr existing))
                    {
                        Handles.Add(existing);
                        Loaded.Add(soname);
                        continue;
                    }

                    string? path = FindLibraryPath(spec, root, directory, soname);
                    if (path is not null && NativeLibrary.TryLoad(path, out IntPtr handle))
                    {
                        Handles.Add(handle);
                        Loaded.Add(soname);
                        loadedNow.Add(path);
                    }
                }
            }

            if (installRoot is null)
            {
                _defaultRootDone = true;
            }
        }

        return loadedNow;
    }

    private static string? FindLibraryPath(
        GpuDependencySpec spec,
        string installRoot,
        string primaryDirectory,
        string soname)
    {
        string path = Path.Combine(primaryDirectory, soname);
        if (File.Exists(path))
        {
            return path;
        }

        // Some wheels (notably cuDNN) put runtime siblings under separate
        // directories in the same pip target, such as nvidia/cublas/lib.
        try
        {
            return Directory.EnumerateFiles(spec.InstallDirectory(installRoot), soname, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Vendoring state of every known GPU dependency under <paramref name="installRoot"/>.</summary>
    internal static IReadOnlyList<GpuDependencyStatus> GetStatus(string installRoot) =>
        KnownGpuDependencies.All
            .Select(spec =>
            {
                string? root = CandidateRoots(installRoot).FirstOrDefault(spec.IsInstalled);
                return new GpuDependencyStatus(
                    spec.Id,
                    spec.Description,
                    root is not null,
                    root is null ? null : spec.LibraryDirectory(root));
            })
            .ToList();
}

/// <summary>Whether one GPU runtime dependency is vendored, and where.</summary>
internal sealed record GpuDependencyStatus(string Id, string Description, bool Installed, string? LibraryDirectory);
