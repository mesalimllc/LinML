using LinML.Contracts;

namespace LinML.Catalog;

/// <summary>
/// A pinned plugin EP wheel: where to get it, how to verify it, and where its
/// library lands once vendored.
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
    Version MinimumOrtVersion)
{
    public string InstallDirectory(string installRoot) => Path.Combine(installRoot, InstallSubdirectory);

    public string LibraryPath(string installRoot) => Path.Combine(InstallDirectory(installRoot), LibraryFileName);

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

    // NVIDIA TensorRT-RTX standalone plugin EP. Works on RTX Ampere+ GPUs.
    // Under WSL2, stock PyPI wheels register but expose zero devices due to
    // virtual PCI bus mapping; see docs/compatibility.md.
    public static readonly PluginPackageSpec TensorrtRtx = new(
        Id: ProviderId.TensorrtRtx,
        RegistrationName: "NvTensorRTRTXExecutionProvider",
        Version: "0.4.0",
        WheelUrl: new Uri("https://files.pythonhosted.org/packages/9c/68/093626a054300fa7f4f1a6d18b775d185034c6bb7fb056ac4f9d1e9b50d4/onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0-py3-none-manylinux_2_28_x86_64.whl"),
        WheelSha256: "68b8f7306b50cf76daf997b8be24ec3813498c15d48e0ddd211db14f2a9daa19",
        PackagePrefix: "onnxruntime_ep_nv_tensorrt_rtx/",
        InstallSubdirectory: Path.Combine("ep-plugins", "trt-rtx", "onnxruntime_ep_nv_tensorrt_rtx"),
        LibraryFileName: "libonnxruntime_providers_nv_tensorrt_rtx.so",
        MinimumOrtVersion: new Version(1, 24, 0));

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

    public static IReadOnlyList<PluginPackageSpec> All { get; } = [OpenVino, TensorrtRtx, MigraphX];
}
