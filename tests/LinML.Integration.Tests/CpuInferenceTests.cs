using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Integration.Tests;

/// <summary>
/// Integration tests for real CPU inference.
/// Gate C: must run actual ONNX model in WSL2 and verify outputs.
/// </summary>
public class CpuInferenceTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "model.onnx");
    private static string FixtureManifestPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "fixture-manifest.json");

    [Fact]
    public void CpuInference_ReturnsExpectedOutput()
    {
        // Skip if fixture not present (not generated yet)
        if (!File.Exists(FixturePath))
        {
            // TODO: Use Xunit.SkippableFact or Assert.SkipWhen in Gate C
            return; // Fixture model not generated. Run scripts/fetch-test-fixture.sh first.
        }

        var modelBytes = File.ReadAllBytes(FixturePath);
        var model = new ModelIdentity
        {
            Path = FixturePath,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modelBytes)).ToLowerInvariant(),
            SizeBytes = modelBytes.Length
        };

        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        using var session = factory.CreateSession(model, ProviderId.Cpu);

        var inputs = new List<TensorInput>
        {
            new()
            {
                Name = "input",
                DataType = TensorDataType.Float32,
                Shape = [3, 2],
                Data = [1, 2, 3, 4, 5, 6]
            }
        };

        var outputs = session.Run(inputs);

        Assert.Single(outputs);
        var output = outputs[0];
        Assert.Equal("output", output.Name);
        Assert.Equal(new[] { 3, 2 }, output.Shape.ToArray());

        // Expected: [[11, 22], [13, 24], [15, 26]]
        var expected = new[] { 11f, 22f, 13f, 24f, 15f, 26f };
        Assert.Equal(expected.Length, output.Data.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], output.Data[i], precision: 5);
        }
    }

    [Fact]
    public void CpuInference_MissingModel_Throws()
    {
        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        var model = new ModelIdentity
        {
            Path = "/nonexistent/model.onnx",
            Sha256 = "0000",
            SizeBytes = 0
        };

        Assert.ThrowsAny<Exception>(() => factory.CreateSession(model, ProviderId.Cpu));
    }

    [Fact]
    public void CpuInference_WrongShape_Throws()
    {
        if (!File.Exists(FixturePath))
        {
            return; // Fixture model not generated.
        }

        var modelBytes = File.ReadAllBytes(FixturePath);
        var model = new ModelIdentity
        {
            Path = FixturePath,
            Sha256 = "test",
            SizeBytes = modelBytes.Length
        };

        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        using var session = factory.CreateSession(model, ProviderId.Cpu);

        var inputs = new List<TensorInput>
        {
            new()
            {
                Name = "input",
                DataType = TensorDataType.Float32,
                Shape = [2, 2], // Wrong shape
                Data = [1, 2, 3, 4]
            }
        };

        Assert.ThrowsAny<Exception>(() => session.Run(inputs));
    }
}
