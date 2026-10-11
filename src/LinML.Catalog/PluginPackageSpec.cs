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

    public static IReadOnlyList<PluginPackageSpec> All { get; } = [OpenVino];
}
