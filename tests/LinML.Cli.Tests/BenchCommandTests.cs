using System.Text.Json;
using Xunit;

namespace LinML.Cli.Tests;

public class BenchCommandTests
{
    private static bool FixturesExist =>
        File.Exists(CliTestHost.Fixture("model.onnx")) &&
        File.Exists(CliTestHost.Fixture("inputs.json"));

    [Fact]
    public async Task Bench_MissingModel_ReturnsExitCode2()
    {
        var (exitCode, _, stderr) = await Task.Run(() => CliTestHost.Run(
            "bench",
            "--model", "nonexistent-model.onnx",
            "--inputs", CliTestHost.Fixture("inputs.json")));

        Assert.Equal(2, exitCode);
        Assert.Contains("Model file not found", stderr);
    }

    [Fact]
    public async Task Bench_MissingInputs_ReturnsExitCode2()
    {
        if (!FixturesExist) return;

        var (exitCode, _, stderr) = await Task.Run(() => CliTestHost.Run(
            "bench",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", "nonexistent-inputs.json"));

        Assert.Equal(2, exitCode);
        Assert.Contains("Inputs file not found", stderr);
    }

    [Fact]
    public async Task Bench_InvalidWarmup_ReturnsExitCode2()
    {
        if (!FixturesExist) return;

        var (exitCode, _, stderr) = await Task.Run(() => CliTestHost.Run(
            "bench",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--warmup", "-1"));

        Assert.Equal(2, exitCode);
        Assert.Contains("--warmup must be greater than or equal to 0", stderr);
    }

    [Fact]
    public async Task Bench_InvalidIterations_ReturnsExitCode2()
    {
        if (!FixturesExist) return;

        var (exitCode, _, stderr) = await Task.Run(() => CliTestHost.Run(
            "bench",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--iterations", "0"));

        Assert.Equal(2, exitCode);
        Assert.Contains("--iterations must be at least 1", stderr);
    }

    [Fact]
    public async Task Bench_CpuProvider_HumanReadable_Succeeds()
    {
        if (!FixturesExist) return;

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "bench",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "cpu",
            "--warmup", "1",
            "--iterations", "5"));

        Assert.Equal(0, exitCode);
        Assert.Contains("LinML Inference Benchmark", stdout);
        Assert.Contains("CPUExecutionProvider", stdout);
        Assert.Contains("inf/s", stdout);
    }

    [Fact]
    public async Task Bench_CpuProvider_Json_ReturnsValidMetrics()
    {
        if (!FixturesExist) return;

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "bench",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "cpu",
            "--warmup", "1",
            "--iterations", "5",
            "--json"));

        Assert.Equal(0, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("warmupIterations").GetInt32());
        Assert.Equal(5, root.GetProperty("measurementIterations").GetInt32());

        var results = root.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());

        var cpuResult = results[0];
        Assert.Equal("CPUExecutionProvider", cpuResult.GetProperty("provider").GetString());
        Assert.True(cpuResult.GetProperty("success").GetBoolean());
        Assert.True(cpuResult.GetProperty("throughputPerSecond").GetDouble() > 0);
        Assert.True(cpuResult.GetProperty("coldStartMs").GetDouble() >= 0);
        Assert.True(cpuResult.GetProperty("p50Ms").GetDouble() >= 0);
        Assert.True(cpuResult.GetProperty("p90Ms").GetDouble() >= 0);
        Assert.True(cpuResult.GetProperty("p99Ms").GetDouble() >= 0);
    }

    [Fact]
    public async Task Bench_AllReady_Json_Succeeds()
    {
        if (!FixturesExist) return;

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            [
                "bench",
                "--model", CliTestHost.Fixture("model.onnx"),
                "--inputs", CliTestHost.Fixture("inputs.json"),
                "--all-ready",
                "--warmup", "1",
                "--iterations", "5",
                "--json",
                .. CliTestHost.FastTensorRtOptions(),
            ]));

        Assert.Equal(0, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        var results = doc.RootElement.GetProperty("results");
        Assert.True(results.GetArrayLength() >= 1);
        // Every ready provider must benchmark cleanly: a provider that fails only
        // because of another provider's leftover CUDA error is a regression.
        foreach (var result in results.EnumerateArray())
        {
            Assert.True(result.GetProperty("success").GetBoolean(),
                $"{result.GetProperty("provider").GetString()}: {result.GetProperty("error").GetString()}");
        }
    }
}
