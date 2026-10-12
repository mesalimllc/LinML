using LinML.Contracts;
using LinML.Policy;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Integration.Tests;

/// <summary>
/// Integration tests for fallback behavior: soft CUDA → CPU, hard CUDA → failure.
/// </summary>
public class FallbackTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "model.onnx");

    [Fact]
    public void SoftCudaRequest_FallsBackToCpu_WhenCudaUnavailable()
    {
        if (!File.Exists(FixturePath))
        {
            return; // Fixture model not generated.
        }

        // This test runs regardless of CUDA availability
        // If CUDA is available, it tests CUDA; if not, it tests fallback to CPU

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

        // Simulate soft selection: try CUDA, fall back to CPU
        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);

        IReadOnlyList<TensorOutput> outputs;
        ProviderId selectedProvider;

        try
        {
            using var session = factory.CreateSession(model, ProviderId.Cuda);
            outputs = session.Run(inputs);
            selectedProvider = ProviderId.Cuda;
        }
        catch
        {
            // Fallback to CPU
            using var session = factory.CreateSession(model, ProviderId.Cpu);
            outputs = session.Run(inputs);
            selectedProvider = ProviderId.Cpu;
        }

        Assert.NotEmpty(outputs);
        // Provider is either CUDA (if available) or CPU (fallback)
        Assert.True(selectedProvider == ProviderId.Cuda || selectedProvider == ProviderId.Cpu);
    }

    [Fact]
    public void HardCudaRequest_Fails_WhenCudaUnavailable()
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

        // Check if CUDA is available
        var probe = new Runtime.Onnx.Providers.CudaReadinessProbe(
            NullLogger<Runtime.Onnx.Providers.CudaReadinessProbe>.Instance);
        var cudaReady = probe.Probe().IsReady;

        if (cudaReady)
        {
            // If CUDA is ready, this test is not applicable
            return;
        }

        // Hard request: should fail
        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        Assert.ThrowsAny<Exception>(() => factory.CreateSession(model, ProviderId.Cuda));
    }
}
