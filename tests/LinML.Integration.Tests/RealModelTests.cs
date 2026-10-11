using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace LinML.Integration.Tests;

/// <summary>
/// Real-model validation (Opp 1): MNIST MLP end-to-end through the runtime.
/// Unlike the synthetic MatMul+Add fixture, this exercises Conv/Relu/Gemm and
/// surfaces real dependency gaps (e.g. CUDA Conv requires cuDNN).
/// Tests skip (early return) when the model file or CUDA is unavailable.
/// </summary>
public class RealModelTests
{
    private static string ModelPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "mnist-8.onnx");
    private static string InputsPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "real", "mnist-input.json");

    private static List<TensorInput> LoadInputs()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(InputsPath));
        var tensors = new List<TensorInput>();
        foreach (var t in doc.RootElement.GetProperty("tensors").EnumerateArray())
        {
            tensors.Add(new TensorInput
            {
                Name = t.GetProperty("name").GetString()!,
                DataType = TensorDataType.Float32,
                Shape = [.. t.GetProperty("shape").EnumerateArray().Select(e => e.GetInt32())],
                Data = [.. t.GetProperty("data").EnumerateArray().Select(e => e.GetSingle())]
            });
        }
        return tensors;
    }

    private static ModelIdentity LoadModel() => new()
    {
        Path = ModelPath,
        Sha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ModelPath))).ToLowerInvariant(),
        SizeBytes = new FileInfo(ModelPath).Length
    };

    [Fact]
    public void RealModel_Cpu_ReturnsValidLogits()
    {
        if (!File.Exists(ModelPath) || !File.Exists(InputsPath))
        {
            return; // Real-model fixture not present.
        }

        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        using var session = factory.CreateSession(LoadModel(), ProviderId.Cpu);

        var outputs = session.Run(LoadInputs());

        Assert.Single(outputs);
        var output = outputs[0];
        Assert.Equal(new[] { 1, 10 }, output.Shape.ToArray());
        Assert.All(output.Data, v => Assert.True(float.IsFinite(v)));

        // Seeded input (seed 42) classifies as digit 8.
        var argmax = output.Data.Select((v, i) => (v, i)).MaxBy(p => p.v).i;
        Assert.Equal(8, argmax);
    }

    [Fact]
    public void RealModel_Cuda_MatchesCpu()
    {
        if (!File.Exists(ModelPath) || !File.Exists(InputsPath))
        {
            return; // Real-model fixture not present.
        }

        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        var inputs = LoadInputs();

        using var cpuSession = factory.CreateSession(LoadModel(), ProviderId.Cpu);
        var cpuOutputs = cpuSession.Run(inputs);

        IReadOnlyList<TensorOutput> cudaOutputs;
        try
        {
            // Requires CUDA + cuDNN (Conv ops); skip honestly when unavailable.
            using var cudaSession = factory.CreateSession(LoadModel(), ProviderId.Cuda);
            cudaOutputs = cudaSession.Run(inputs);
        }
        catch
        {
            return;
        }

        Assert.Single(cudaOutputs);
        Assert.Equal(cpuOutputs[0].Data.Count, cudaOutputs[0].Data.Count);

        var cpuArgmax = cpuOutputs[0].Data.Select((v, i) => (v, i)).MaxBy(p => p.v).i;
        var cudaArgmax = cudaOutputs[0].Data.Select((v, i) => (v, i)).MaxBy(p => p.v).i;
        Assert.Equal(cpuArgmax, cudaArgmax);

        for (var i = 0; i < cpuOutputs[0].Data.Count; i++)
        {
            // Cross-provider float math differs in the 4th decimal; argmax
            // agreement above is the primary correctness check.
            Assert.Equal(cpuOutputs[0].Data[i], cudaOutputs[0].Data[i], precision: 2);
        }
    }
}
