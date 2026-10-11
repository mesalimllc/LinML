using LinML.Contracts;
using LinML.Runtime.Onnx.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Runtime.Onnx.Tests;

/// <summary>
/// Tests for CudaReadinessProbe.
/// Gate D: readiness must test actual ORT provider availability, not just nvidia-smi.
/// </summary>
public class CudaReadinessProbeTests
{
    [Fact]
    public void Probe_ReturnsReport_WithProviderId()
    {
        var probe = new CudaReadinessProbe(NullLogger<CudaReadinessProbe>.Instance);
        var report = probe.Probe();

        Assert.Equal(ProviderId.Cuda, report.Provider);
    }

    [Fact]
    public void Probe_ReportsPhaseResults()
    {
        var probe = new CudaReadinessProbe(NullLogger<CudaReadinessProbe>.Instance);
        var report = probe.Probe();

        Assert.NotEmpty(report.PhaseResults);
    }

    [Fact]
    public void Probe_WhenNotReady_ReportsFailedPhase()
    {
        var probe = new CudaReadinessProbe(NullLogger<CudaReadinessProbe>.Instance);
        var report = probe.Probe();

        if (!report.IsReady)
        {
            Assert.NotNull(report.FailedPhase);
            Assert.NotNull(report.FailureReason);
        }
    }
}
