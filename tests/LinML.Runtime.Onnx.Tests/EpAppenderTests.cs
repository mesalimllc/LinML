using LinML.Contracts;
using LinML.Runtime.Onnx;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace LinML.Runtime.Onnx.Tests;

/// <summary>
/// How <see cref="EpAppender"/> appends providers. The plugin-EP cases assume no
/// TensorRT-RTX or OpenVINO plugin is registered in the test process, which
/// holds unless the opt-in test below runs in the same process with
/// LINML_OPENVINO_EP_LIB set (it registers OpenVINO, so the unregistered-OpenVINO
/// test skips itself then).
/// </summary>
public class EpAppenderTests
{
    private static bool OpenVinoRegistered() =>
        OrtEnv.Instance().GetEpDevices().Any(d => d.EpName == ProviderId.OpenVino.Value);

    [Fact]
    public void Cpu_AppendsNothing_AndSucceeds()
    {
        using var options = new SessionOptions();
        EpAppender.Append(options, ProviderId.Cpu, options: null, epLibraries: null);
    }

    [Fact]
    public void TensorRtRtx_WithoutPluginDevices_FailsWithActionableMessage()
    {
        if (OrtEnv.Instance().GetEpDevices().Any(d => d.EpName == ProviderId.TensorrtRtx.Value))
        {
            return; // A TRT-RTX plugin is registered on this host; nothing to assert.
        }

        using var options = new SessionOptions();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EpAppender.Append(options, ProviderId.TensorrtRtx, options: null, epLibraries: null));

        Assert.Contains("exposes no EP devices", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PluginEpWithoutDevices_FallsBackToNameBasedAppend()
    {
        if (OpenVinoRegistered())
        {
            return;
        }

        // No OpenVINO devices registered: the generic path appends by name, and
        // the ORT 1.31 GPU build does not compile OpenVINO in, so ORT rejects it.
        // This is the failure a host sees until it registers the plugin library.
        using var options = new SessionOptions();
        var ex = Assert.ThrowsAny<Exception>(() =>
            EpAppender.Append(options, ProviderId.OpenVino, options: null, epLibraries: null));

        Assert.Contains("OpenVINO", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownProvider_IsRejectedByOrt()
    {
        using var options = new SessionOptions();
        Assert.ThrowsAny<Exception>(() =>
            EpAppender.Append(options, new ProviderId("NopeExecutionProvider"), options: null, epLibraries: null));
    }

    /// <summary>
    /// Opt-in: set LINML_OPENVINO_EP_LIB to a vendored
    /// libonnxruntime_providers_openvino_plugin.so (onnxruntime-ep-openvino 1.6.1).
    /// </summary>
    [Fact]
    public void RegisteredPlugin_AppendsViaDevices_AndRegistryIsIdempotent()
    {
        string? lib = Environment.GetEnvironmentVariable("LINML_OPENVINO_EP_LIB");
        if (string.IsNullOrEmpty(lib) || !File.Exists(lib))
        {
            return;
        }

        var libraries = new Dictionary<string, string> { ["test.openvino"] = lib };
        PluginEpRegistry.EnsureRegistered(libraries);
        PluginEpRegistry.EnsureRegistered(libraries); // same name and path: no-op

        Assert.Contains("test.openvino", PluginEpRegistry.RegisteredNames);
        Assert.Throws<ArgumentException>(() => PluginEpRegistry.EnsureRegistered(
            new Dictionary<string, string> { ["test.openvino"] = Path.Combine(Path.GetDirectoryName(lib)!, "libopenvino.so.2621") }));

        using var options = new SessionOptions();
        EpAppender.Append(options, ProviderId.OpenVino, options: null, epLibraries: null);

        string model = Path.Combine(AppContext.BaseDirectory, "fixtures", "model.onnx");
        if (File.Exists(model))
        {
            using var session = new InferenceSession(model, options);
            Assert.NotEmpty(session.InputMetadata);
        }
    }
}
