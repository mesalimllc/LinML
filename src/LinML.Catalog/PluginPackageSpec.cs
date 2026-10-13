using System.Runtime.InteropServices;
using System.Text.Json;
using LinML.Contracts;

namespace LinML.Catalog;

/// <summary>
/// A pinned plugin EP wheel: where to get it, how to verify it, and where its
/// library lands once vendored. <c>Architecture</c> is the Linux host architecture
/// the wheel is built for, as a RID suffix: <c>x64</c> or <c>arm64</c>.
/// <c>MinimumGlibc</c>, when set, is the oldest glibc the wheel loads on.
/// <c>VersionManifestFile</c>, when set, names a JSON file in the package whose <c>epVersion</c> records the
/// version it was built as: an install only counts as vendored when that equals <c>Version</c>.
/// </summary>
internal sealed record PluginPackageSpec(
    ProviderId Id,
    string RegistrationName,
    string Version,
    Uri WheelUrl,
    string WheelSha256,
    string PackagePrefix,
    string InstallSubdirectory,
    string LibraryFileName,
    Version MinimumOrtVersion,
    string Architecture = "x64",
    Version? MinimumGlibc = null,
    string? VersionManifestFile = null)
{
    public string InstallDirectory(string installRoot) => Path.Combine(installRoot, InstallSubdirectory);

    public string LibraryPath(string installRoot) => Path.Combine(InstallDirectory(installRoot), LibraryFileName);

    /// <summary>
    /// The version a vendored library was built as, read from <see cref="VersionManifestFile"/> beside it, or
    /// null when this package has no manifest, the file is missing or it is unreadable.
    /// </summary>
    public string? VendoredVersion(string libraryPath)
    {
        string? directory = Path.GetDirectoryName(libraryPath);
        if (VersionManifestFile is null || directory is null)
        {
            return null;
        }

        try
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, VersionManifestFile)));
            return manifest.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("epVersion", out JsonElement version)
                && version.ValueKind == JsonValueKind.String
                    ? version.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the library at <paramref name="libraryPath"/> is the pinned build. A package without a
    /// <see cref="VersionManifestFile"/> has nothing to compare, so any library counts. With one, the vendored
    /// version must equal <see cref="Version"/>: successive builds share one install directory, and a library
    /// that is merely present may be an earlier build the pin replaced.
    /// </summary>
    public bool IsCurrent(string libraryPath) =>
        VersionManifestFile is null || string.Equals(VendoredVersion(libraryPath), Version, StringComparison.Ordinal);

    /// <summary>Whether the pinned build is vendored under <paramref name="installRoot"/>.</summary>
    public bool IsVendored(string installRoot) =>
        File.Exists(LibraryPath(installRoot)) && IsCurrent(LibraryPath(installRoot));

    public static readonly IReadOnlyList<string> DefaultCandidateRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "linml-deps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "linml"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "linml", "ep-plugins")
    ];

    public string? FindLibrary(string? customRoot = null)
    {
        var candidateRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(customRoot))
        {
            candidateRoots.Add(customRoot);
        }

        var envPlugin = Environment.GetEnvironmentVariable("LINML_PLUGIN_ROOT");
        if (!string.IsNullOrWhiteSpace(envPlugin))
        {
            candidateRoots.Add(envPlugin);
        }

        var envDeps = Environment.GetEnvironmentVariable("LINML_DEPS");
        if (!string.IsNullOrWhiteSpace(envDeps))
        {
            candidateRoots.Add(envDeps);
        }

        candidateRoots.AddRange(DefaultCandidateRoots);

        foreach (var root in candidateRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var standardPath = LibraryPath(root);
            if (File.Exists(standardPath))
            {
                return standardPath;
            }

            if (InstallSubdirectory.StartsWith("ep-plugins" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var relSub = InstallSubdirectory.Substring("ep-plugins".Length + 1);
                var subPath = Path.Combine(root, relSub, LibraryFileName);
                if (File.Exists(subPath))
                {
                    return subPath;
                }
            }

            var directPath = Path.Combine(root, LibraryFileName);
            if (File.Exists(directPath))
            {
                return directPath;
            }
        }

        return null;
    }
}

/// <summary>Plugin EPs the catalog knows. Keep in sync with <c>scripts/fetch-ep-plugins.sh</c>.</summary>
internal static class KnownPlugins
{
    // 1.6.1, not 1.7.0: 1.7.0 needs the experimental OrtEpApi EpContextConfig
    // functions (SinceV28) that ORT 1.31 does not expose, and fails to create
    // the EP. Same pin, URL and digest as scripts/fetch-ep-plugins.sh.
    public static readonly PluginPackageSpec OpenVino = new(
        Id: ProviderId.OpenVino,
        RegistrationName: "linml.openvino",
        Version: "1.6.1",
        WheelUrl: new Uri("https://files.pythonhosted.org/packages/80/50/691cfc0370ab26a62213741026a8e7a172215e02b37cb2ff73ab1b0e1e60/onnxruntime_ep_openvino-1.6.1-py3-none-manylinux_2_28_x86_64.whl"),
        WheelSha256: "a1afbbad0f051523bac0ab10b0aa8f49112f60f2c008f2ffb6c43eaf435412e0",
        PackagePrefix: "onnxruntime_ep_openvino/",
        InstallSubdirectory: Path.Combine("ep-plugins", "openvino", "onnxruntime_ep_openvino"),
        LibraryFileName: "libonnxruntime_providers_openvino_plugin.so",
        MinimumOrtVersion: new Version(1, 23, 0));

    // NVIDIA TensorRT-RTX standalone plugin EP. Works on RTX Ampere+ GPUs. The
    // wheel is per architecture: x86_64 for desktop and workstation hosts, and
    // aarch64 for Linux SBSA hosts (TensorRT-RTX 1.6 supports Ubuntu 24.04 SBSA).
    // Under WSL2, stock PyPI wheels register but expose zero devices due to
    // virtual PCI bus mapping; see docs/compatibility.md.
    internal static readonly PluginPackageSpec TensorrtRtxLinuxX64 = new(
        Id: ProviderId.TensorrtRtx,
        RegistrationName: "NvTensorRTRTXExecutionProvider",
        Version: "0.4.0",
        WheelUrl: new Uri("https://files.pythonhosted.org/packages/9c/68/093626a054300fa7f4f1a6d18b775d185034c6bb7fb056ac4f9d1e9b50d4/onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_28_x86_64.whl"),
        WheelSha256: "68b8f7306b50cf76daf997b8be24ec3813498c15d48e0ddd211db14f2a9daa19",
        PackagePrefix: "onnxruntime_ep_nv_tensorrt_rtx/",
        InstallSubdirectory: Path.Combine("ep-plugins", "trt-rtx", "onnxruntime_ep_nv_tensorrt_rtx"),
        LibraryFileName: "libonnxruntime_providers_nv_tensorrt_rtx.so",
        MinimumOrtVersion: new Version(1, 24, 0),
        Architecture: "x64");

    // The same EP for Linux aarch64 (SBSA). Same version and ABI, and the same
    // bundled TensorRT-RTX 1.6.1 runtime, built for aarch64 (manylinux_2_35, so
    // glibc 2.35 or newer). Installs beside the x64 build, not over it. Not yet
    // verified on aarch64 hardware.
    internal static readonly PluginPackageSpec TensorrtRtxLinuxArm64 = new(
        Id: ProviderId.TensorrtRtx,
        RegistrationName: "NvTensorRTRTXExecutionProvider",
        Version: "0.4.0",
        WheelUrl: new Uri("https://files.pythonhosted.org/packages/41/63/ef110d73730969957740d770b29c158ca79a9df32c65ee081b01f8b64124/onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_35_aarch64.whl"),
        WheelSha256: "0ccf6a636f9f49d74e3e7e3b1b90287aa1c818db770b47fad6e8aa344f4290a5",
        PackagePrefix: "onnxruntime_ep_nv_tensorrt_rtx/",
        InstallSubdirectory: Path.Combine("ep-plugins", "trt-rtx-arm64", "onnxruntime_ep_nv_tensorrt_rtx"),
        LibraryFileName: "libonnxruntime_providers_nv_tensorrt_rtx.so",
        MinimumOrtVersion: new Version(1, 24, 0),
        Architecture: "arm64",
        MinimumGlibc: new Version(2, 35));

    /// <summary>The TensorRT-RTX pin for this host: the wheel built for its architecture.</summary>
    public static PluginPackageSpec TensorrtRtx =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? TensorrtRtxLinuxArm64 : TensorrtRtxLinuxX64;

    // Standalone MIGraphX plugin EP built from onnxruntime-ep-amdgpu by
    // scripts/build-migraphx-ep.sh and published to LinML releases. Requires
    // ROCm with MIGraphX >= 2.17 (libmigraphx_c.so.3) on the host.
    public static readonly PluginPackageSpec MigraphX = new(
        Id: ProviderId.MigraphX,
        RegistrationName: "MIGraphXExecutionProvider",
        Version: "2611.1",
        WheelUrl: new Uri("https://github.com/trackdubllc/LinML/releases/download/ep-migraphx-2611.1/linml-migraphx-ep-2611.1-linux-x64.zip"),
        WheelSha256: "968bdb1aa294c1ab4af04cfec0d4318fb2ffa4c69f0b34bc3b98532a199acd86",
        PackagePrefix: "linml_migraphx_ep/",
        InstallSubdirectory: Path.Combine("ep-plugins", "migraphx", "linml_migraphx_ep"),
        LibraryFileName: "libmigraphx-ep.so",
        MinimumOrtVersion: new Version(1, 24, 0));

    // Opt-in LinML-hosted patched TensorRT-RTX EP for WSL2 (release
    // ep-trt-rtx-wsl2-0.4.0-wsl2p2, built by scripts/build-trt-rtx-ep-docker.sh; fixes
    // device enumeration on the WSL2 virtual PCI bus, upstream PR #12). Same EP
    // name and ABI as the stock wheel, so it is a drop-in replacement and is
    // deliberately NOT in All: it is not a separate provider, and installing it
    // is opt-in. It dynamically links the TensorRT-RTX runtime that only the
    // stock wheel bundles, so TensorrtRtx must be vendored too. Same pin, URL
    // and digest as fetch_trt_rtx_wsl2 in scripts/fetch-ep-plugins.sh.
    public static readonly PluginPackageSpec TensorrtRtxWsl2 = new(
        Id: ProviderId.TensorrtRtx,
        RegistrationName: "NvTensorRTRTXExecutionProvider",
        Version: "0.4.0-wsl2p2",
        WheelUrl: new Uri("https://github.com/trackdubllc/LinML/releases/download/ep-trt-rtx-wsl2-0.4.0-wsl2p2/linml-trt-rtx-ep-0.4.0-wsl2p2-linux-x64.zip"),
        WheelSha256: "7575c51157110228d581593c04147473868fbb7eefc79aa1b809c5b8bb66c220",
        PackagePrefix: "linml_trt_rtx_ep/",
        InstallSubdirectory: Path.Combine("ep-plugins", "trt-rtx-wsl2", "linml_trt_rtx_ep"),
        LibraryFileName: "libonnxruntime_providers_nv_tensorrt_rtx.so",
        MinimumOrtVersion: new Version(1, 24, 0),
        VersionManifestFile: "build-info.json");

    // Qualcomm QNN plugin EP (onnxruntime/onnxruntime-qnn v2.6.0), Linux aarch64
    // inference package. The tarball is flat: the EP plugin .so plus the bundled
    // QAIRT 2.50.40 runtime (libQnnHtp.so, HTP stubs/skels). The EP resolves its
    // bundled libraries via RUNPATH $ORIGIN. The linux-x64 tarball is AOT
    // compilation only and cannot run inference, so this pin is aarch64-only: on
    // x86_64 hosts the catalog reports it as not applicable. Unverified on hardware
    // (no Snapdragon NPU available). Same pin, URL and digest as fetch_qnn in
    // scripts/fetch-ep-plugins.sh.
    public static readonly PluginPackageSpec Qnn = new(
        Id: ProviderId.Qnn,
        RegistrationName: "QNNExecutionProvider",
        Version: "2.6.0",
        WheelUrl: new Uri("https://github.com/onnxruntime/onnxruntime-qnn/releases/download/v2.6.0/onnxruntime-qnn-2.6.0-linux-aarch64.tgz"),
        WheelSha256: "c61507ec7f9fa0eb2c0c1cefe5a0e8bdf48f185504de224917b23a389f362b38",
        PackagePrefix: "",
        InstallSubdirectory: Path.Combine("ep-plugins", "qnn", "onnxruntime_qnn"),
        LibraryFileName: "libonnxruntime_providers_qnn.so",
        MinimumOrtVersion: new Version(1, 24, 1),
        Architecture: "arm64");

    public static IReadOnlyList<PluginPackageSpec> All { get; } = [OpenVino, TensorrtRtx, MigraphX, Qnn];

    /// <summary>
    /// The opted-in WSL2 build for <paramref name="spec"/>, if one is vendored, this
    /// host is WSL2, and <paramref name="spec"/> is the x86_64 TensorRT-RTX wheel (the
    /// patched build is x86_64 only); otherwise null. Preferred over the stock wheel,
    /// which registers but exposes no devices under WSL2.
    /// </summary>
    internal static string? WslVariantLibrary(PluginPackageSpec spec, string? root = null)
    {
        if (spec.Id != ProviderId.TensorrtRtx || spec.Architecture != "x64"
            || !PluginExecutionProvider.IsWsl() || PluginExecutionProvider.Wsl2OptedOut())
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(root))
        {
            string own = TensorrtRtxWsl2.LibraryPath(root);
            if (File.Exists(own))
            {
                return own;
            }

            // An explicit root that vendors the stock wheel keeps it; the search only
            // widens to the default roots when the root has neither build.
            if (File.Exists(spec.LibraryPath(root)))
            {
                return null;
            }
        }

        return TensorrtRtxWsl2.FindLibrary(root);
    }

    /// <summary>
    /// Directory of the stock wheel's runtime libraries, which the WSL2 build links
    /// against by SONAME, or null if the stock wheel is not vendored.
    /// </summary>
    internal static string? WslVariantRuntimeDirectory(string? root = null)
    {
        string? stock = TensorrtRtx.FindLibrary(root);
        return stock is null ? null : Path.GetDirectoryName(stock);
    }
}
