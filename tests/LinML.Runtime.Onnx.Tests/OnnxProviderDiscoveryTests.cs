using LinML.Runtime.Onnx;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Runtime.Onnx.Tests;

/// <summary>
/// Tests for OnnxProviderDiscovery.
/// Gate D: discovery must report providers from ORT, not just nvidia-smi.
/// </summary>
public class OnnxProviderDiscoveryTests
{
    [Fact]
    public void Discover_ReturnsRuntimeVersion()
    {
        var discovery = new OnnxProviderDiscovery(NullLogger<OnnxProviderDiscovery>.Instance);
        var report = discovery.Discover();

        Assert.False(string.IsNullOrEmpty(report.RuntimeVersion));
    }

    [Fact]
    public void Discover_AlwaysIncludesCpuProvider()
    {
        var discovery = new OnnxProviderDiscovery(NullLogger<OnnxProviderDiscovery>.Instance);
        var report = discovery.Discover();

        Assert.Contains(report.AvailableProviders, p => p.Value == "CPUExecutionProvider");
    }

    [Fact]
    public void Discover_ReportsDiagnostics()
    {
        var discovery = new OnnxProviderDiscovery(NullLogger<OnnxProviderDiscovery>.Instance);
        var report = discovery.Discover();

        Assert.NotNull(report.Diagnostics);
    }
}
