using System.Text.Json;
using Xunit;

namespace LinML.Cli.Tests;

/// <summary>
/// Real CLI parsing tests (Opp 3): unknown commands, missing options, and
/// invalid values must fail with non-zero exit codes instead of crashing.
/// </summary>
public class CommandParsingTests
{
    [Fact]
    public async Task UnknownCommand_ReturnsNonZero()
    {
        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run("frobnicate"));
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Run_MissingRequiredOptions_ReturnsNonZero()
    {
        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run("run"));
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Run_UnknownProvider_Returns1()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx"))
            || !File.Exists(CliTestHost.Fixture("inputs.json")))
        {
            return;
        }

        // Unknown provider name hits the ArgumentException path (exit 1).
        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "tpu"));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Run_ProviderAliasTrt_AlwaysSucceeds()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx"))
            || !File.Exists(CliTestHost.Fixture("inputs.json")))
        {
            return;
        }

        // TRT EP is compiled into our ORT build and listed even without
        // libnvinfer present; selection/execution fall back honestly to
        // CUDA then CPU. The run must succeed with correct outputs either way.
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "trt",
            "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("outputs")[0].GetProperty("data")
            .EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], data);
    }

    [Fact]
    public async Task Run_RawEpName_Works()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx"))
            || !File.Exists(CliTestHost.Fixture("inputs.json")))
        {
            return;
        }

        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "CPUExecutionProvider"));

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Run_BadEpLibrary_FailsWithError()
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
            "--provider", "trt-rtx",
            "--ep-library", "NvTensorRTRTXExecutionProvider=/nonexistent/lib.so"));

        Assert.Equal(1, exitCode);
        Assert.Contains("EP plugin library not found", stderr);
    }

    [Fact]
    public async Task Run_BadEpOption_FailsWithError()
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
            "--provider", "cpu",
            "--ep-option", "no-equals-sign"));

        Assert.Equal(1, exitCode);
        Assert.Contains("key=value", stderr);
    }

    [Fact]
    public async Task Providers_UnknownSubcommand_ReturnsNonZero()
    {
        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run("providers", "bogus"));
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Run_ProviderAliasTrtRtx_Soft_AlwaysSucceeds()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx"))
            || !File.Exists(CliTestHost.Fixture("inputs.json")))
        {
            return;
        }

        // TRT-RTX may be absent (no plugin, no EP device, no RTX GPU): soft
        // selection must fall back honestly and still produce correct outputs.
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "trt-rtx",
            "--json"));

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("outputs")[0].GetProperty("data")
            .EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], data);
    }

    [Fact]
    public async Task Run_ProviderAliasTrtRtx_Hard_FailsOrRuns()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx"))
            || !File.Exists(CliTestHost.Fixture("inputs.json")))
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.Run(
            "run",
            "--model", CliTestHost.Fixture("model.onnx"),
            "--inputs", CliTestHost.Fixture("inputs.json"),
            "--provider", "trt-rtx",
            "--require-provider",
            "--json"));

        // Either a real TRT-RTX device ran (0) or the hard requirement failed
        // honestly (3). Never a silent fallback, never a crash.
        Assert.True(exitCode is 0 or 3, $"Unexpected exit code {exitCode}");
        if (exitCode == 0)
        {
            using var doc = JsonDocument.Parse(stdout);
            Assert.Equal("NvTensorRTRTXExecutionProvider",
                doc.RootElement.GetProperty("selectedProvider").GetString());
        }
    }
}
