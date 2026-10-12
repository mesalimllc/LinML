using LinML.Cli.Commands;
using LinML.Contracts;
using Xunit;

namespace LinML.Cli.Tests;

public class DoctorHintsTests
{
    private static ProviderReadinessReport NotReady(string reason) => new()
    {
        Provider = ProviderId.Tensorrt,
        IsReady = false,
        FailedPhase = ReadinessPhase.Append,
        FailureReason = reason,
    };

    private static ProviderReadinessReport Ready() => new() { Provider = ProviderId.Cpu, IsReady = true };

    [Fact]
    public void Build_VendoredPresentAndLoaderFailure_ReturnsHintWithEnvScript()
    {
        var hints = DoctorHints.Build(
            [NotReady("libnvinfer.so.10: cannot open shared object file")],
            vendoredPresent: true,
            envScriptPath: "/opt/linml/scripts/env-gpu-deps.sh");

        Assert.Contains(hints, h => h.Contains("source /opt/linml/scripts/env-gpu-deps.sh"));
    }

    [Fact]
    public void Build_NoEnvScriptLocated_FallsBackToRelativePath()
    {
        var hints = DoctorHints.Build(
            [NotReady("OrtSessionOptionsAppendExecutionProvider_TensorRT: Failed to load shared library")],
            vendoredPresent: true,
            envScriptPath: null);

        Assert.Contains(hints, h => h.Contains("source scripts/env-gpu-deps.sh"));
    }

    [Fact]
    public void Build_ProviderNotListedByRuntime_ReturnsEmpty()
    {
        // e.g. OpenVINO on a non-Intel CPU: registered but exposes no devices.
        var hints = DoctorHints.Build(
            [NotReady("Provider 'OpenVINOExecutionProvider' is not listed by the ORT runtime.")],
            vendoredPresent: true,
            envScriptPath: null);

        Assert.Empty(hints);
    }

    [Fact]
    public void Build_NothingVendored_ReturnsEmpty()
    {
        var hints = DoctorHints.Build(
            [NotReady("libnvinfer.so.10: cannot open shared object file")],
            vendoredPresent: false,
            envScriptPath: null);

        Assert.Empty(hints);
    }

    [Fact]
    public void Build_FailureUnrelatedToLoading_ReturnsEmpty()
    {
        var hints = DoctorHints.Build(
            [NotReady("Smoke inference produced wrong output"), Ready()],
            vendoredPresent: true,
            envScriptPath: null);

        Assert.Empty(hints);
    }

    [Fact]
    public void Build_AllReady_ReturnsEmpty()
    {
        Assert.Empty(DoctorHints.Build([Ready()], vendoredPresent: true, envScriptPath: null));
    }
}
