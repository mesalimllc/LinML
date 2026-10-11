using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit;

namespace LinML.Catalog.Tests;

/// <summary>
/// Downloads the real OpenVINO plugin (~160 MB) and runs MNIST on it. Opt-in:
/// set LINML_CATALOG_NETWORK_TESTS=1. Otherwise the test returns without
/// asserting, so CI does not download on every run.
/// </summary>
public class OpenVinoCatalogEndToEndTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("LINML_CATALOG_NETWORK_TESTS") == "1";

    [Fact]
    public async Task EnsureReady_ThenAppendFromCatalog_RunsMnistOnOpenVino()
    {
        if (!Enabled || !OperatingSystem.IsLinux())
        {
            return;
        }

        using var root = new TempDir();
        ExecutionProviderCatalog catalog = ExecutionProviderCatalog.Create(root.Path);
        IExecutionProvider openVino = catalog.Find(Contracts.ProviderId.OpenVino)!;

        ProviderEnsureResult ensured = await openVino.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true));
        Assert.Equal(ProviderEnsureStatus.Downloaded, ensured.Status);
        Assert.NotEmpty(ensured.Devices);
        Assert.Equal(ProviderReadyState.Ready, openVino.ReadyState);

        ProviderEnsureResult again = await openVino.EnsureReadyAsync(new EnsureOptions(AllowDownloads: false));
        Assert.Equal(ProviderEnsureStatus.Ready, again.Status);

        // A second catalog with an empty root reuses the existing registration
        // instead of registering a second copy (which would need its own
        // library and would give the append devices from two EP factories).
        using var otherRoot = new TempDir();
        IExecutionProvider second = ExecutionProviderCatalog.Create(otherRoot.Path).Find(Contracts.ProviderId.OpenVino)!;
        Assert.Equal(ProviderReadyState.Ready, second.ReadyState);
        ProviderEnsureResult reused = await second.EnsureReadyAsync(new EnsureOptions(AllowDownloads: false));
        Assert.Equal(ProviderEnsureStatus.Ready, reused.Status);
        Assert.Equal(ensured.Devices.Count, reused.Devices.Count);

        using var options = new SessionOptions();
        ProviderAppendResult appended = options.AppendFromCatalog(openVino, [OrtHardwareDeviceType.CPU]);
        Assert.True(appended.Appended, appended.FailureReason);

        string fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "real");
        using var session = new InferenceSession(Path.Combine(fixtures, "mnist-8.onnx"), options);
        float[] input = ReadMnistInput(Path.Combine(fixtures, "mnist-input.json"));
        var tensor = new DenseTensor<float>(input, [1, 1, 28, 28]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs =
            session.Run([NamedOnnxValue.CreateFromTensor("Input3", tensor)]);

        float[] logits = outputs.First().AsEnumerable<float>().ToArray();
        Assert.Equal(10, logits.Length);
        Assert.Equal(8, Array.IndexOf(logits, logits.Max()));
    }

    private static float[] ReadMnistInput(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("tensors")[0].GetProperty("data")
            .EnumerateArray().Select(e => e.GetSingle()).ToArray();
    }
}
