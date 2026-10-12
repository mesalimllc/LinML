namespace LinML.Catalog;

/// <summary>
/// A pinned native GPU runtime dependency (cuDNN, TensorRT) that is vendored as a
/// pip wheel into <c>~/.local/linml-deps</c>. Mirrors the pins in
/// <c>scripts/setup-gpu-deps.sh</c>; <c>ScriptPinAgreementTests</c> keeps them equal.
/// </summary>
internal sealed record GpuDependencySpec(
    string Id,
    string Description,
    string PipPackage,
    string? IndexUrl,
    string InstallSubdirectory,
    string SentinelLibrary,
    IReadOnlyList<string> PreloadLibraries)
{
    public string InstallDirectory(string installRoot) => Path.Combine(installRoot, InstallSubdirectory);

    /// <summary>The file whose presence marks the dependency as fully vendored.</summary>
    public string SentinelPath(string installRoot) => Path.Combine(InstallDirectory(installRoot), SentinelLibrary);

    public bool IsInstalled(string installRoot) => File.Exists(SentinelPath(installRoot));

    /// <summary>Directory holding the sentinel and preloaded libraries (the loader-path entry).</summary>
    public string LibraryDirectory(string installRoot) =>
        Path.GetDirectoryName(SentinelPath(installRoot))!;
}

/// <summary>GPU runtime dependencies LinML can vendor. Keep in sync with <c>scripts/setup-gpu-deps.sh</c>.</summary>
internal static class KnownGpuDependencies
{
    private const string NvidiaIndex = "https://pypi.nvidia.com/";

    /// <summary>cuDNN 9 (CUDA 13), required by CUDA EP Conv kernels.</summary>
    public static readonly GpuDependencySpec Cudnn = new(
        Id: "cudnn",
        Description: "cuDNN 9 (CUDA 13) for CUDA EP Conv kernels",
        PipPackage: "nvidia-cudnn-cu13==9.27.0.42",
        IndexUrl: null,
        InstallSubdirectory: "cudnn",
        SentinelLibrary: Path.Combine("nvidia", "cudnn", "lib", "libcudnn.so.9"),
        // cuDNN's cuBLAS dependencies are installed in the sibling cublas/lib directory.
        PreloadLibraries: ["libcublas.so.13", "libcublasLt.so.13", "libcudnn.so.9"]);

    /// <summary>TensorRT 10 classic runtime (<c>libnvinfer.so.10</c>), required by the TensorRT EP.</summary>
    public static readonly GpuDependencySpec TensorRt = new(
        Id: "tensorrt",
        Description: "TensorRT 10 (CUDA 13) for the TensorRT EP",
        PipPackage: "tensorrt-cu13-libs==10.16.1.11",
        IndexUrl: NvidiaIndex,
        InstallSubdirectory: "tensorrt-libs",
        SentinelLibrary: Path.Combine("tensorrt_libs", "libnvinfer.so.10"),
        PreloadLibraries: ["libnvinfer.so.10", "libnvinfer_plugin.so.10", "libnvonnxparser.so.10"]);

    /// <summary>TensorRT 10 lean runtime.</summary>
    public static readonly GpuDependencySpec TensorRtLean = new(
        Id: "tensorrt-lean",
        Description: "TensorRT 10 lean runtime (CUDA 13)",
        PipPackage: "tensorrt-lean-cu13-libs==10.16.1.11",
        IndexUrl: null,
        InstallSubdirectory: "tensorrt-lean",
        SentinelLibrary: Path.Combine("tensorrt_lean_libs", "libnvinfer_lean.so.10"),
        PreloadLibraries: []);

    public static IReadOnlyList<GpuDependencySpec> All { get; } = [Cudnn, TensorRt, TensorRtLean];
}
