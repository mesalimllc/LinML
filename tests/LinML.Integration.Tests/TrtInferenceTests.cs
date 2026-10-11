using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Integration.Tests;

/// <summary>
/// Integration tests for the classic TensorRT execution provider.
/// Skips honestly when libnvinfer (TensorRT 10.x) is not on the loader path:
/// the EP is compiled into the ORT build and listed, but session creation
/// fails without the native libs — listed != runnable.
/// </summary>
public class TrtInferenceTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "model.onnx");
    private static string MnistPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "mnist-8.onnx");
    private static string MnistInputsPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "mnist-input.json");

    private static bool HasFixture() => File.Exists(FixturePath);
    private static bool HasMnist() => File.Exists(MnistPath) && File.Exists(MnistInputsPath);

    private static ModelIdentity Identify(string path) => new()
    {
        Path = path,
        Sha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
        SizeBytes = new FileInfo(path).Length
    };

    private static List<TensorInput> TinyInputs() =>
    [
        new TensorInput
        {
            Name = "input",
            DataType = TensorDataType.Float32,
            Shape = [3, 2],
            Data = [1, 2, 3, 4, 5, 6]
        }
    ];

    private static List<TensorInput> MnistInputs()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(MnistInputsPath));
        var t = doc.RootElement.GetProperty("tensors")[0];
        return
        [
            new TensorInput
            {
                Name = t.GetProperty("name").GetString()!,
                DataType = TensorDataType.Float32,
                Shape = [.. t.GetProperty("shape").EnumerateArray().Select(e => e.GetInt32())],
                Data = [.. t.GetProperty("data").EnumerateArray().Select(e => e.GetSingle())]
            }
        ];
    }

    private static bool TrtRunnable()
    {
        if (!HasFixture())
        {
            return false;
        }
        try
        {
            var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
            using var session = factory.CreateSession(Identify(FixturePath), ProviderId.Tensorrt);
            return true;
        }
        catch
        {
            return false; // libnvinfer not present
        }
    }

    [Fact]
    public void Trt_TinyFixture_ReturnsExpectedOutput()
    {
        if (!TrtRunnable())
        {
            return; // TensorRT native libs not installed.
        }

        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        using var session = factory.CreateSession(Identify(FixturePath), ProviderId.Tensorrt);

        var outputs = session.Run(TinyInputs());

        Assert.Single(outputs);
        var expected = new[] { 11f, 22f, 13f, 24f, 15f, 26f };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], outputs[0].Data[i], precision: 4);
        }
    }

    [Fact]
    public void Trt_Mnist_MatchesCpu()
    {
        if (!HasMnist() || !TrtRunnable())
        {
            return;
        }

        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        var model = Identify(MnistPath);
        var inputs = MnistInputs();

        using var cpuSession = factory.CreateSession(model, ProviderId.Cpu);
        var cpuOutputs = cpuSession.Run(inputs);

        // Note: ORT 1.31's TRT EP has no trt_tf32_enable option (removed);
        // TRT 10 defaults to TF32 tensor cores, so logits differ from CPU
        // FP32 in the ~3rd decimal. Argmax agreement is the primary check,
        // with a TF32-class absolute tolerance.
        using var trtSession = factory.CreateSession(model, ProviderId.Tensorrt);
        var trtOutputs = trtSession.Run(inputs);

        Assert.Single(trtOutputs);
        var cpuArgmax = cpuOutputs[0].Data.Select((v, i) => (v, i)).MaxBy(p => p.v).i;
        var trtArgmax = trtOutputs[0].Data.Select((v, i) => (v, i)).MaxBy(p => p.v).i;
        Assert.Equal(cpuArgmax, trtArgmax);

        for (var i = 0; i < cpuOutputs[0].Data.Count; i++)
        {
            Assert.True(
                Math.Abs(cpuOutputs[0].Data[i] - trtOutputs[0].Data[i]) <= 0.01,
                $"Logit {i}: cpu={cpuOutputs[0].Data[i]} trt={trtOutputs[0].Data[i]}");
        }
    }

    [Fact]
    public void Trt_Profiling_ReportsTensorrtPlacement()
    {
        if (!TrtRunnable())
        {
            return;
        }

        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        using var session = factory.CreateSession(
            Identify(FixturePath), ProviderId.Tensorrt, enableProfiling: true);

        try
        {
            session.Run(TinyInputs());
            var placements = session.EndProfiling();

            Assert.NotEmpty(placements);
            Assert.Contains(placements, p => p.Provider == "TensorrtExecutionProvider");
        }
        finally
        {
            if (session.ProfilePath is not null && File.Exists(session.ProfilePath))
            {
                File.Delete(session.ProfilePath);
            }
        }
    }
}
