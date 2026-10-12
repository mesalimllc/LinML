using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Integration.Tests;

/// <summary>
/// Tests for ModelSmokeRunner (Opp 4): real per-model smoke per provider.
/// CUDA assertions run only where CUDA truly executes; otherwise they skip.
/// </summary>
public class ModelSmokeTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "model.onnx");
    private static string InputsPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "inputs.json");

    private static bool HasFixture() => File.Exists(FixturePath) && File.Exists(InputsPath);

    private static (ModelIdentity Model, List<TensorInput> Inputs) Load()
    {
        var modelBytes = File.ReadAllBytes(FixturePath);
        var model = new ModelIdentity
        {
            Path = FixturePath,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modelBytes)).ToLowerInvariant(),
            SizeBytes = modelBytes.Length
        };
        var inputs = new List<TensorInput>
        {
            new()
            {
                Name = "x",
                DataType = TensorDataType.Float32,
                Shape = [3, 2],
                Data = [1, 2, 3, 4, 5, 6]
            }
        };
        return (model, inputs);
    }

    private static ModelSmokeRunner CreateRunner() => new(
        new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance),
        NullLogger<ModelSmokeRunner>.Instance);

    [Fact]
    public void Smoke_Cpu_PassesAllPhases()
    {
        if (!HasFixture())
        {
            return;
        }

        var (model, inputs) = Load();
        var report = CreateRunner().RunSmoke(ProviderId.Cpu, model, inputs);

        Assert.True(report.IsReady);
        Assert.Equal(3, report.PhaseResults.Count);
        Assert.All(report.PhaseResults, r => Assert.True(r.Success));
    }

    [Fact]
    public void Smoke_Cuda_ReportsHonestResult()
    {
        if (!HasFixture())
        {
            return;
        }

        var (model, inputs) = Load();
        var report = CreateRunner().RunSmoke(ProviderId.Cuda, model, inputs);

        // Either CUDA really ran (all phases pass) or it failed at a named
        // phase with a reason — never a silent or placeholder pass.
        if (report.IsReady)
        {
            Assert.All(report.PhaseResults, r => Assert.True(r.Success));
        }
        else
        {
            Assert.NotNull(report.FailedPhase);
            Assert.False(string.IsNullOrWhiteSpace(report.FailureReason));
        }
    }

    [Fact]
    public void Smoke_UnknownProvider_FailsLoad()
    {
        if (!HasFixture())
        {
            return;
        }

        var (model, inputs) = Load();
        var report = CreateRunner().RunSmoke(new ProviderId("NopeExecutionProvider"), model, inputs);

        Assert.False(report.IsReady);
        Assert.Equal(ReadinessPhase.Load, report.FailedPhase);
    }

    [Fact]
    public void Smoke_WrongInputs_FailsSmokePhase()
    {
        if (!HasFixture())
        {
            return;
        }

        var (model, _) = Load();
        var badInputs = new List<TensorInput>
        {
            new()
            {
                Name = "x",
                DataType = TensorDataType.Float32,
                Shape = [2, 2],
                Data = [1, 2, 3, 4]
            }
        };

        var report = CreateRunner().RunSmoke(ProviderId.Cpu, model, badInputs);

        Assert.False(report.IsReady);
        Assert.Equal(ReadinessPhase.Smoke, report.FailedPhase);
    }
}
