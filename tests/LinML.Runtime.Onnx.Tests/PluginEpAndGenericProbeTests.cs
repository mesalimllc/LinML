using LinML.Contracts;
using LinML.Runtime.Onnx;
using LinML.Runtime.Onnx.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinML.Runtime.Onnx.Tests;

/// <summary>
/// Tests for PluginEpRegistry and GenericReadinessProbe.
/// </summary>
public class PluginEpAndGenericProbeTests
{
    [Fact]
    public void EnsureRegistered_NullOrEmpty_IsNoOp()
    {
        PluginEpRegistry.EnsureRegistered(null);
        PluginEpRegistry.EnsureRegistered(new Dictionary<string, string>());
    }

    [Fact]
    public void EnsureRegistered_MissingFile_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            PluginEpRegistry.EnsureRegistered(
                new Dictionary<string, string> { ["bogus"] = "/nonexistent/libbogus.so" }));
    }

    [Fact]
    public void EnsureRegistered_SameNameTwice_IsIdempotent()
    {
        // A bogus path throws on first registration and must not be recorded,
        // so a second call retries (and throws again) rather than silently passing.
        var libs = new Dictionary<string, string> { ["bogus-twice"] = "/nonexistent/libbogus.so" };
        Assert.Throws<FileNotFoundException>(() => PluginEpRegistry.EnsureRegistered(libs));
        Assert.Throws<FileNotFoundException>(() => PluginEpRegistry.EnsureRegistered(libs));
        Assert.DoesNotContain("bogus-twice", PluginEpRegistry.RegisteredNames);
    }

    [Fact]
    public void GenericProbe_UnknownProvider_FailsLoadHonestly()
    {
        var probe = new GenericReadinessProbe(NullLogger.Instance);
        var report = probe.Probe(new ProviderId("NopeExecutionProvider"));

        Assert.False(report.IsReady);
        Assert.Equal(ReadinessPhase.Load, report.FailedPhase);
        Assert.False(string.IsNullOrWhiteSpace(report.FailureReason));
    }

    [Fact]
    public void GenericProbe_Cpu_PassesLoadAndAppend_WithoutClaimingSmoke()
    {
        var probe = new GenericReadinessProbe(NullLogger.Instance);
        var report = probe.Probe(ProviderId.Cpu);

        Assert.True(report.IsReady);
        Assert.Equal(2, report.PhaseResults.Count);
        Assert.All(report.PhaseResults, r => Assert.True(r.Success));
        // Smoke is not claimed without a model (honesty: no placeholder pass).
        Assert.DoesNotContain(report.PhaseResults, r => r.Phase == ReadinessPhase.Smoke);
    }
}
