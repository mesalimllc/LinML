using System.Text.Json;
using Xunit;

namespace LinML.Cli.Tests;

/// <summary>
/// Real CLI exit-code tests (Opp 3): invoke Program.Main and assert exit codes,
/// replacing the Gate E placeholder assertions. Exit codes: 0 success,
/// 1 execution failure, 2 invalid arguments, 3 required provider unavailable,
/// 4 unexpected/internal error.
/// </summary>
public class ExitCodeTests
{
    private static bool HasTinyFixture() =>
        File.Exists(CliTestHost.Fixture("model.onnx")) && File.Exists(CliTestHost.Fixture("inputs.json"));

    [Fact]
    public async Task Run_Cpu_Returns0_WithExpectedOutput()
    {
        if (!HasTinyFixture())
        {
            return; // Fixture not generated. Run scripts/fetch-test-fixture.sh first.
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "cpu",
            "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("CPUExecutionProvider", root.GetProperty("selectedProvider").GetString());

        var data = root.GetProperty("outputs")[0].GetProperty("data")
            .EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], data);
    }

    [Fact]
    public async Task Run_MissingModel_Returns2()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("does-not-exist.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "cpu"));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Run_MissingInputs_Returns2()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("does-not-exist.json"),
            "--provider", "cpu"));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Run_WrongShape_Returns1()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var badInputs = Path.Combine(Path.GetTempPath(), $"linml-bad-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(badInputs,
            """{"tensors": [{"name": "x", "type": "float32", "shape": [2, 2], "data": [1, 2, 3, 4]}]}""");
        try
        {
            var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run(
                "run",
                "--model", CliTestHost.Fixture("model.onnx"),
                "--inputs", badInputs,
                "--provider", "cpu"));

            Assert.Equal(1, exitCode);
        }
        finally
        {
            File.Delete(badInputs);
        }
    }

    [Fact]
    public async Task Run_CudaSoft_AlwaysSucceeds()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        // Soft selection: succeeds via CUDA or via CPU fallback — never fails here.
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "cuda",
            "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("outputs")[0].GetProperty("data")
            .EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], data);
    }

    [Fact]
    public async Task Run_CudaHard_ConsistentWithAvailability()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "cuda",
            "--require-provider",
            "--json"));

        // Either CUDA ran (0) or the hard requirement failed honestly (3).
        Assert.True(exitCode is 0 or 3, $"Unexpected exit code {exitCode}");
        if (exitCode == 0)
        {
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal("CUDAExecutionProvider",
                doc.RootElement.GetProperty("selectedProvider").GetString());
        }
    }

    [Fact]
    public async Task ProvidersList_Returns0_JsonShape()
    {
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "providers", "list", "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        var providers = doc.RootElement.GetProperty("availableProviders")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("CPUExecutionProvider", providers);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("runtimeVersion").GetString()));
    }

    [Fact]
    public async Task Doctor_Returns0_JsonShape()
    {
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "doctor", "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        Assert.True(doc.RootElement.TryGetProperty("discovery", out _));
        Assert.True(doc.RootElement.TryGetProperty("readiness", out _));
        Assert.True(doc.RootElement.TryGetProperty("catalog", out var catalog));
        Assert.True(catalog.GetArrayLength() > 0);

        var migraphx = catalog.EnumerateArray().First(p => p.GetProperty("id").GetString() == "migraphx");
        Assert.Contains("linml catalog install migraphx", migraphx.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task Doctor_TextOutput_IncludesCatalogPluginHints()
    {
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "doctor"));

        Assert.Equal(0, exitCode);
        Assert.Contains("Catalog plugins:", stdout);
        Assert.Contains("Run 'linml catalog install migraphx' to vendor missing libraries.", stdout);
    }

    [Fact]
    public async Task Run_MissingRequiredPlugin_Returns3_WithInstallHint()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx"))
            || !File.Exists(CliTestHost.Fixture("inputs.json")))
        {
            return;
        }

        var (exitCode, _, stderr) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "migraphx",
            "--require-provider"));

        Assert.Equal(3, exitCode);
        Assert.Contains("Run 'linml catalog install migraphx' to vendor missing libraries.", stderr);
    }

    [Fact]
    public async Task Doctor_ModelSmoke_Returns0_WithPhaseResults()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx"))
            || !File.Exists(CliTestHost.Fixture("inputs.json")))
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            [
                "doctor",
                "--model", CliTestHost.Fixture("model.onnx"),
                "--inputs", CliTestHost.Fixture("inputs.json"),
                "--json",
                .. CliTestHost.FastTensorRtOptions(),
            ]));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        var readiness = doc.RootElement.GetProperty("readiness").EnumerateArray().ToArray();
        Assert.NotEmpty(readiness);

        // CPU smoke must always pass; CUDA passes iff the device really runs.
        var cpu = readiness.Single(r =>
            r.GetProperty("provider").GetString() == "CPUExecutionProvider");
        Assert.True(cpu.GetProperty("isReady").GetBoolean());
    }

    [Fact]
    public async Task Doctor_ModelWithoutInputs_Returns2()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx")))
        {
            return;
        }

        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run(
            "doctor",
            "--model", CliTestHost.Fixture("model.onnx")));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Version_Returns0()
    {
        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run("--version"));
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Run_RequiredProviderNotInRuntime_Returns3_WithReason()
    {
        // LINML_EP_LIBRARIES (e.g. from sourcing scripts/env-gpu-deps.sh) makes
        // the CLI register plugins before selection, which can change the exit
        // path; the test asserts the plain-runtime contract only.
        if (!HasTinyFixture() || !OperatingSystem.IsLinux()
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LINML_EP_LIBRARIES")))
        {
            return;
        }

        // QNN is never compiled into the Linux x64 ORT build, so the hard
        // requirement must fail with exit 3 on every Linux host, and must not
        // fall back to another provider.
        var (exitCode, stdout, stderr) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "qnn",
            "--require-provider",
            "--json"));

        Assert.Equal(3, exitCode);
        Assert.Contains("QNNExecutionProvider", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("\"outputs\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_Profile_ReportsPerNodePlacement()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "cpu",
            "--profile",
            "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        string[] profile = doc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Where(d => d.GetProperty("phase").GetString() == "profile")
            .Select(d => d.GetProperty("message").GetString()!)
            .ToArray();

        const string writtenPrefix = "ORT profile written to ";
        string? profilePath = profile.FirstOrDefault(m => m.StartsWith(writtenPrefix, StringComparison.Ordinal))?[writtenPrefix.Length..];
        try
        {
            Assert.NotNull(profilePath);
            Assert.Single(profile, m => m.StartsWith(writtenPrefix, StringComparison.Ordinal));
            Assert.Contains(profile, m => m.StartsWith("Node '", StringComparison.Ordinal)
                && m.EndsWith("ran on CPUExecutionProvider", StringComparison.Ordinal));
            Assert.True(File.Exists(profilePath), $"Profile file missing: {profilePath}");
        }
        finally
        {
            if (profilePath is not null)
            {
                File.Delete(profilePath);
            }
        }
    }
}
