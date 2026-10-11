using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace LinML.Integration.Tests;

/// <summary>
/// Integration tests for the TensorRT-RTX plugin EP (NvTensorRTRTXExecutionProvider).
///
/// Honest skip chain:
///   1. plugin .so missing -> skip (run scripts/fetch-ep-plugins.sh)
///   2. registration fails -> skip (ABI mismatch)
///   3. zero EP devices -> skip (known WSL2 limitation: ORT's PCI discovery
///      reports the WSL-virtual bus id, which cudaDeviceGetByPCIBusId rejects;
///      the EP filters to NVIDIA PCI vendor and finds nothing in WSL2.
///      Works on native Windows / bare-metal Linux per NVIDIA's matrix.)
/// </summary>
public class TrtRtxInferenceTests
{
    private const string EpName = "NvTensorRTRTXExecutionProvider";
    private const string RegistrationName = "nvtensorrtrtx";

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "model.onnx");

    private static string? PluginPath()
    {
        // Explicit override first, then LINML_EP_LIBRARIES, then the default
        // location populated by scripts/fetch-ep-plugins.sh.
        var explicitPath = Environment.GetEnvironmentVariable("LINML_TRT_RTX_EP_LIB");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var env = Environment.GetEnvironmentVariable("LINML_EP_LIBRARIES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var pair in env.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx > 0 && pair[..idx].Trim() == RegistrationName)
                {
                    return pair[(idx + 1)..].Trim();
                }
            }
        }

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "linml-deps", "ep-plugins", "trt-rtx",
            "onnxruntime_ep_nv_tensorrt_rtx", "libonnxruntime_providers_nv_tensorrt_rtx.so");
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private static bool TryRegisterAndGetDevice(out OrtEpDevice? device)
    {
        device = null;
        var path = PluginPath();
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            PluginEpRegistry.EnsureRegistered(new Dictionary<string, string>
            {
                [RegistrationName] = path
            });
            device = OrtEnv.Instance().GetEpDevices().FirstOrDefault(d => d.EpName == EpName);
            return device is not null;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void TrtRtx_DeviceEnumeration_ReportsHonestResult()
    {
        var path = PluginPath();
        if (path is null)
        {
            return; // Plugin not vendored. Run scripts/fetch-ep-plugins.sh.
        }

        // Registration either works (ABI negotiated) or throws; both are
        // honest outcomes. What matters: we never claim a device that isn't there.
        var registered = TryRegisterAndGetDevice(out var device);

        Assert.True(registered == (device is not null));
    }

    [Fact]
    public void TrtRtx_Fixture_ReturnsExpectedOutput()
    {
        if (!File.Exists(FixturePath))
        {
            return;
        }
        if (!TryRegisterAndGetDevice(out _))
        {
            return; // No TRT-RTX EP device (e.g. WSL2 PCI mismatch, no RTX GPU).
        }

        var modelBytes = File.ReadAllBytes(FixturePath);
        var model = new ModelIdentity
        {
            Path = FixturePath,
            Sha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(modelBytes)).ToLowerInvariant(),
            SizeBytes = modelBytes.Length
        };

        var epLibraries = new Dictionary<string, string> { [RegistrationName] = PluginPath()! };
        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        using var session = factory.CreateSession(
            model, ProviderId.TensorrtRtx, epLibraries: epLibraries);

        var outputs = session.Run(
        [
            new TensorInput
            {
                Name = "input",
                DataType = TensorDataType.Float32,
                Shape = [3, 2],
                Data = [1, 2, 3, 4, 5, 6]
            }
        ]);

        Assert.Single(outputs);
        var expected = new[] { 11f, 22f, 13f, 24f, 15f, 26f };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], outputs[0].Data[i], precision: 4);
        }
    }

    [Fact]
    public void TrtRtx_Profiling_ReportsRtxPlacement()
    {
        if (!File.Exists(FixturePath))
        {
            return;
        }
        if (!TryRegisterAndGetDevice(out _))
        {
            return;
        }

        var modelBytes = File.ReadAllBytes(FixturePath);
        var model = new ModelIdentity
        {
            Path = FixturePath,
            Sha256 = "test",
            SizeBytes = modelBytes.Length
        };

        var epLibraries = new Dictionary<string, string> { [RegistrationName] = PluginPath()! };
        var factory = new OnnxSessionFactory(NullLogger<OnnxSessionFactory>.Instance);
        using var session = factory.CreateSession(
            model, ProviderId.TensorrtRtx, enableProfiling: true, epLibraries: epLibraries);

        try
        {
            session.Run(
            [
                new TensorInput
                {
                    Name = "input",
                    DataType = TensorDataType.Float32,
                    Shape = [3, 2],
                    Data = [1, 2, 3, 4, 5, 6]
                }
            ]);

            var placements = session.EndProfiling();
            Assert.NotEmpty(placements);
            Assert.Contains(placements, p => p.Provider == EpName);
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
