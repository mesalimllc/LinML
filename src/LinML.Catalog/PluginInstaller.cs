using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LinML.Catalog;

/// <summary>
/// Vendors a pinned plugin wheel: download, sha256 verification, and a flat
/// extract of the package's native files into a staging directory that is
/// renamed into place only when complete. C# port of the per-plugin steps in
/// <c>scripts/fetch-ep-plugins.sh</c>, so the product needs no bash or Python.
/// </summary>
internal sealed class PluginInstaller(HttpMessageHandler? httpHandler = null)
{
    private readonly HttpMessageHandler? _httpHandler = httpHandler;

    /// <summary>Installs the plugin unless its library is already present.</summary>
    /// <returns>True if this call vendored it; false if it was already present.</returns>
    /// <exception cref="InvalidDataException">The download failed sha256 verification.</exception>
    public async Task<bool> InstallAsync(PluginPackageSpec spec, string installRoot, CancellationToken cancellationToken)
    {
        string finalDir = spec.InstallDirectory(installRoot);
        if (File.Exists(spec.LibraryPath(installRoot)))
        {
            return false;
        }

        string parent = Path.GetDirectoryName(finalDir)!;
        Directory.CreateDirectory(parent);

        // Unique per attempt so a concurrent installer never deletes or moves
        // this attempt's files.
        string stage = $"{finalDir}.partial-{Guid.NewGuid():N}";
        string wheel = stage + ".whl";
        try
        {
            await DownloadAndVerifyAsync(spec, wheel, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(stage);
            ExtractFlat(wheel, spec.PackagePrefix, stage);
            CreateRequiredSymlinks(stage);
            if (!File.Exists(Path.Combine(stage, spec.LibraryFileName)))
            {
                throw new InvalidDataException(
                    $"Wheel for {spec.Id} did not contain {spec.PackagePrefix}{spec.LibraryFileName}.");
            }

            return Promote(stage, finalDir, spec.LibraryFileName);
        }
        finally
        {
            TryDeleteFile(wheel);
            TryDeleteDirectory(stage);
        }
    }

    private async Task DownloadAndVerifyAsync(PluginPackageSpec spec, string destination, CancellationToken cancellationToken)
    {
        using var client = _httpHandler is null ? new HttpClient() : new HttpClient(_httpHandler, disposeHandler: false);
        client.Timeout = TimeSpan.FromMinutes(15);

        using HttpResponseMessage response = await client
            .GetAsync(spec.WheelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = new byte[1 << 16];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, spec.WheelSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"sha256 mismatch for {spec.WheelUrl}: expected {spec.WheelSha256}, got {actual}.");
        }
    }

    /// <summary>
    /// Copies the package's top-level native files (<c>*.so</c>, <c>*.so.*</c>)
    /// and runtime data (<c>*.json</c>) into <paramref name="destination"/>,
    /// stripping the package prefix. Python sources, dist-info and nested
    /// directories are skipped.
    /// </summary>
    internal static void ExtractFlat(string wheelPath, string packagePrefix, string destination)
    {
        using ZipArchive zip = ZipFile.OpenRead(wheelPath);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(packagePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string name = entry.FullName[packagePrefix.Length..];
            if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal) || !IsVendoredFile(name))
            {
                continue;
            }

            entry.ExtractToFile(Path.Combine(destination, name), overwrite: false);
        }
    }

    private static bool IsVendoredFile(string name) =>
        name.EndsWith(".so", StringComparison.Ordinal)
        || name.Contains(".so.", StringComparison.Ordinal)
        || name.EndsWith(".json", StringComparison.Ordinal);

    /// <summary>
    /// Loads native libraries shipped beside a plugin before the plugin itself is
    /// registered. This makes bundled dependencies available without requiring
    /// the caller to set LD_LIBRARY_PATH before starting the process.
    /// </summary>
    internal static IReadOnlyList<IntPtr> LoadBundledLibraries(string directory, string pluginFileName)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(directory))
        {
            return [];
        }

        List<IntPtr> handles = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.so*"))
        {
            if (string.Equals(Path.GetFileName(path), pluginFileName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                handles.Add(NativeLibrary.Load(path));
            }
            catch (DllNotFoundException)
            {
                // A bundled library may depend on a host driver library. The
                // plugin registration below reports that failure if necessary.
            }
            catch (BadImageFormatException)
            {
                // Leave the normal registration path to report an invalid binary.
            }
        }

        return handles;
    }

    /// <summary>
    /// Creates SONAME symlinks that plugin libraries (like TensorRT-RTX) require at runtime.
    /// </summary>
    internal static void CreateRequiredSymlinks(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string[] sonamePairs =
        [
            "libtensorrt_rtx.so.1.6.1", "libtensorrt_rtx.so.1",
            "libtensorrt_onnxparser_rtx.so.1.6.1", "libtensorrt_onnxparser_rtx.so.1",
            "libcudart.so.13.4.36", "libcudart.so.13"
        ];

        for (int i = 0; i < sonamePairs.Length; i += 2)
        {
            string targetName = sonamePairs[i];
            string linkName = sonamePairs[i + 1];
            string targetPath = Path.Combine(directory, targetName);
            string linkPath = Path.Combine(directory, linkName);

            if (File.Exists(targetPath) && !File.Exists(linkPath))
            {
                try
                {
                    File.CreateSymbolicLink(linkPath, targetName);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="stage"/> to <paramref name="finalDir"/>. The rename
    /// itself picks the winner between concurrent installers, so a completed
    /// install is never deleted: installs only ever appear complete, and a
    /// directory without the library can only be a leftover from an older,
    /// interrupted or nested install, which is moved aside and the rename retried.
    /// </summary>
    /// <returns>True if the stage was moved into place; false if another installer finished first.</returns>
    internal static bool Promote(string stage, string finalDir, string libraryFileName)
    {
        string library = Path.Combine(finalDir, libraryFileName);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Directory.Move(stage, finalDir);
                return true;
            }
            catch (IOException) when (Directory.Exists(finalDir))
            {
                if (File.Exists(library))
                {
                    return false;
                }

                string aside = $"{finalDir}.stale-{Guid.NewGuid():N}";
                try
                {
                    Directory.Move(finalDir, aside);
                }
                catch (IOException)
                {
                    // A concurrent installer moved the leftover first.
                }

                TryDeleteDirectory(aside);
            }
        }

        return File.Exists(library)
            ? false
            : throw new IOException($"Could not move {stage} into place at {finalDir}.");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
