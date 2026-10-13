using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace LinML.Catalog.Tests;

/// <summary>
/// Acceptance test for native WSL2 TensorRT-RTX support: from a clean root, EnsureReadyAsync
/// downloads the stock wheel and the patched build and exposes the GPU, then a model runs on it.
/// Opt-in: set LINML_CATALOG_NETWORK_TESTS=1 on a WSL2 host with NVIDIA GPU passthrough
/// (/dev/dxg and nvidia-smi). Otherwise the test returns without asserting.
/// </summary>
[Collection("WslProbe")]
public class TensorrtRtxWsl2EndToEndTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("LINML_CATALOG_NETWORK_TESTS") == "1"
        && OperatingSystem.IsLinux()
        && File.Exists("/dev/dxg")
        && (File.Exists("/usr/lib/wsl/lib/nvidia-smi")
            || (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Any(dir => File.Exists(Path.Combine(dir, "nvidia-smi"))));

    [Fact]
    public async Task EnsureReady_FromCleanRoot_ExposesTheGpu_AndRunsTheFixtureModel()
    {
        if (!Enabled)
        {
            return;
        }

        using var root = new TempDir();
        IExecutionProvider trtRtx = ExecutionProviderCatalog.Create(root.Path).Find(Contracts.ProviderId.TensorrtRtx)!;

        ProviderEnsureResult ensured = await trtRtx.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));
        Assert.Equal(ProviderEnsureStatus.Downloaded, ensured.Status);
        Assert.NotEmpty(ensured.Devices);
        Assert.Contains("wsl2", trtRtx.LibraryPath, StringComparison.OrdinalIgnoreCase);

        using var options = new SessionOptions();
        ProviderAppendResult appended = options.AppendFromCatalog(trtRtx, [OrtHardwareDeviceType.GPU]);
        Assert.True(appended.Appended, appended.FailureReason);

        using var session = new InferenceSession(Path.Combine(AppContext.BaseDirectory, "fixtures", "model.onnx"), options);
        var input = new DenseTensor<float>(new float[] { 1, 2, 3, 4, 5, 6 }, [3, 2]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            session.Run([NamedOnnxValue.CreateFromTensor("x", input)]);

        Assert.Equal(new float[] { 11, 22, 13, 24, 15, 26 }, outputs.First().AsEnumerable<float>().ToArray());
    }
}
