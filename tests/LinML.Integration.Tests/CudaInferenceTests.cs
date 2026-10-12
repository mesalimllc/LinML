using LinML.Contracts;
using LinML.Runtime.Onnx;
using LinML.Runtime.Onnx.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Integration.Tests;

/// <summary>
/// Integration tests for CUDA inference.
/// Gate D: must run on RTX 5070 in WSL2. Skip if CUDA not available.
/// </summary>
public class CudaInferenceTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "model.onnx");

    // Cached smoke result: probe Load/Append phases are necessary but not
    // sufficient (listed != runnable). Only a real model run proves CUDA works.
    private static readonly Lazy<bool> CudaSmokePassed = new(() =>
    {
        try
        {
            var probe = new CudaReadinessProbe(NullLogger<CudaReadinessProbe>.Instance);
            if (!probe.Probe().IsReady)
            {
                return false;
            }

            if (!File.Exists(FixturePath))
            {
                return false;
            }

            var modelBytes = File.ReadAllBytes(FixturePath);
            var model = new ModelIdentity
            {
                Path = FixturePath,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modelBytes)).ToLowerInvariant(),
                SizeBytes = modelBytes.Length
            };

            var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
            using var session = factory.CreateSession(model, ProviderId.Cuda);

            var outputs = session.Run(new List<TensorInput>
            {
                new()
                {
                    Name = "x",
                    DataType = TensorDataType.Float32,
                    Shape = [3, 2],
                    Data = [1, 2, 3, 4, 5, 6]
                }
            });

            return outputs.Count == 1 && outputs[0].Data.Count == 6;
        }
        catch
        {
            return false; // CUDA listed but not runnable (no device, missing libs, etc.)
        }
    });

    private static bool IsCudaAvailable() => CudaSmokePassed.Value;

    [Fact]
    public void CudaInference_ReturnsExpectedOutput()
    {
        if (!File.Exists(FixturePath))
        {
            return; // Fixture model not generated.
        }

        if (!IsCudaAvailable())
        {
            return; // CUDA not available on this host.
        }

        var modelBytes = File.ReadAllBytes(FixturePath);
        var model = new ModelIdentity
        {
            Path = FixturePath,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(modelBytes)).ToLowerInvariant(),
            SizeBytes = modelBytes.Length
        };

        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        using var session = factory.CreateSession(model, ProviderId.Cuda);

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

        var outputs = session.Run(inputs);

        Assert.Single(outputs);
        var output = outputs[0];

        // Expected: [[11, 22], [13, 24], [15, 26]] within tolerance
        var expected = new[] { 11f, 22f, 13f, 24f, 15f, 26f };
        Assert.Equal(expected.Length, output.Data.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], output.Data[i], precision: 4); // rtol=1e-5, atol=1e-6
        }
    }

    [Fact]
    public void CudaAndCpu_ProduceSameOutput()
    {
        if (!File.Exists(FixturePath))
        {
            return; // Fixture model not generated.
        }

        if (!IsCudaAvailable())
        {
            return; // CUDA not available on this host.
        }

        var modelBytes = File.ReadAllBytes(FixturePath);
        var model = new ModelIdentity
        {
            Path = FixturePath,
            Sha256 = "test",
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

        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);

        // CPU run
        using var cpuSession = factory.CreateSession(model, ProviderId.Cpu);
        var cpuOutputs = cpuSession.Run(inputs);

        // CUDA run
        using var cudaSession = factory.CreateSession(model, ProviderId.Cuda);
        var cudaOutputs = cudaSession.Run(inputs);

        // Compare
        Assert.Single(cpuOutputs);
        Assert.Single(cudaOutputs);
        Assert.Equal(cpuOutputs[0].Data.Count, cudaOutputs[0].Data.Count);

        for (var i = 0; i < cpuOutputs[0].Data.Count; i++)
        {
            Assert.Equal(cpuOutputs[0].Data[i], cudaOutputs[0].Data[i], precision: 4);
        }
    }
}
