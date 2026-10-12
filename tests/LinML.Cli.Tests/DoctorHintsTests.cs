using LinML.Cli.Commands;
using LinML.Contracts;
using Xunit;

namespace LinML.Cli.Tests;

public class DoctorHintsTests
{
    private const string NvinferFailure = "libnvinfer.so.10: cannot open shared object file";

    private static ProviderReadinessReport NotReady(string reason) => new()
    {
        Provider = ProviderId.Tensorrt,
        IsReady = false,
        FailedPhase = ReadinessPhase.Append,
        FailureReason = reason,
    };

    private static ProviderReadinessReport Ready() => new() { Provider = ProviderId.Cpu, IsReady = true };

    private static IReadOnlyList<string> Build(
        IEnumerable<ProviderReadinessReport> readiness,
        string[]? catalogFailures = null,
        string[]? gpuDeps = null,
        bool pluginInstalled = false,
        string? script = null,
        string? root = null) =>
        DoctorHints.Build(readiness, catalogFailures ?? [], gpuDeps ?? [], pluginInstalled, script, root);

    [Fact]
    public void Build_VendoredLibraryFailsToLoad_ReturnsHintWithEnvScript()
    {
        var hints = Build([NotReady(NvinferFailure)], gpuDeps: ["tensorrt"], script: "/opt/linml/scripts/env-gpu-deps.sh");

        Assert.Contains("  source /opt/linml/scripts/env-gpu-deps.sh", hints);
    }

    [Fact]
    public void Build_NoEnvScriptLocated_FallbackCommandIsCopyPasteable()
    {
        var hints = Build(
            [NotReady("OrtSessionOptionsAppendExecutionProvider_TensorRT: Failed to load shared library")],
            gpuDeps: ["tensorrt"]);

        Assert.Contains("  source scripts/env-gpu-deps.sh", hints);
        // The explanation must not share a line with the command.
        Assert.DoesNotContain(hints, h => h.Contains("source ") && h.Contains('('));
    }

    [Fact]
    public void Build_ScriptPathWithSpaces_IsShellQuoted()
    {
        var hints = Build([NotReady(NvinferFailure)], gpuDeps: ["tensorrt"], script: "/opt/My LinML/it's/env-gpu-deps.sh");

        Assert.Contains("  source '/opt/My LinML/it'\\''s/env-gpu-deps.sh'", hints);
    }

    [Fact]
    public void Build_CustomRoot_IsPassedAsDeps()
    {
        var hints = Build([NotReady(NvinferFailure)], gpuDeps: ["tensorrt"], script: "/s/env.sh", root: "/data/my deps");

        Assert.Contains("  DEPS='/data/my deps' source /s/env.sh", hints);
    }

    [Fact]
    public void Build_PluginRegistrationFailureInCatalog_ReturnsHint()
    {
        var hints = Build(
            [Ready()],
            catalogFailures: ["libnvonnxruntime_trt_rtx.so: cannot open shared object file"],
            pluginInstalled: true);

        Assert.NotEmpty(hints);
    }

    [Fact]
    public void Build_FailingLibraryNotVendored_ReturnsEmpty()
    {
        // cuDNN is vendored but the failure is about TensorRT's libnvinfer, which is not.
        var hints = Build([NotReady(NvinferFailure)], gpuDeps: ["cudnn"]);

        Assert.Empty(hints);
    }

    [Fact]
    public void Build_NothingVendored_ReturnsEmpty()
    {
        Assert.Empty(Build([NotReady("some.so: cannot open shared object file")]));
    }

    [Fact]
    public void Build_ProviderNotListedByRuntime_ReturnsEmpty()
    {
        // e.g. OpenVINO on a non-Intel CPU: registered but exposes no devices.
        var hints = Build(
            [NotReady("Provider 'OpenVINOExecutionProvider' is not listed by the ORT runtime.")],
            gpuDeps: ["tensorrt"],
            pluginInstalled: true);

        Assert.Empty(hints);
    }

    [Fact]
    public void Build_FailureUnrelatedToLoading_ReturnsEmpty()
    {
        var hints = Build([NotReady("Smoke inference produced wrong output"), Ready()], gpuDeps: ["tensorrt"]);

        Assert.Empty(hints);
    }

    [Fact]
    public void Build_AllReady_ReturnsEmpty()
    {
        Assert.Empty(Build([Ready()], gpuDeps: ["tensorrt"], pluginInstalled: true));
    }
}
