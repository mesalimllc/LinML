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
            """{"tensors": [{"name": "input", "type": "float32", "shape": [2, 2], "data": [1, 2, 3, 4]}]}""");
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
    }

    [Fact]
    public async Task Version_Returns0()
    {
        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run("--version"));
        Assert.Equal(0, exitCode);
    }
}
