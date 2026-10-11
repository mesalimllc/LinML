using System.Text.Json;
using Xunit;

namespace LinML.Cli.Tests;

/// <summary>
/// Tests for `linml serve`: long-lived session over JSON-lines stdio,
/// sharing selection/fallback semantics with `run`.
/// </summary>
public class ServeCommandTests
{
    private static bool HasTinyFixture() =>
        File.Exists(CliTestHost.Fixture("model.onnx")) && File.Exists(CliTestHost.Fixture("inputs.json"));

    private static string TinyRequestLine()
    {
        var json = File.ReadAllText(CliTestHost.Fixture("inputs.json"));
        // Single line: the serve protocol is one JSON document per line.
        return json.Replace("\n", "").Replace("\r", "");
    }

    private static float[] OutputData(string responseLine)
    {
        using var doc = JsonDocument.Parse(responseLine);
        return doc.RootElement.GetProperty("outputs")[0].GetProperty("data")
            .EnumerateArray().Select(e => e.GetSingle()).ToArray();
    }

    [Fact]
    public async Task Serve_TwoRequests_BothSucceedWithExpectedOutputs()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var request = TinyRequestLine();
        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            [request, request],
            "serve", "--model", CliTestHost.Fixture("model.onnx"), "--provider", "cpu"));

        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], OutputData(line));
        }

        // Both responses select the same provider: the session is held, not recreated.
        using var first = JsonDocument.Parse(lines[0]);
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal(
            first.RootElement.GetProperty("selectedProvider").GetString(),
            second.RootElement.GetProperty("selectedProvider").GetString());
    }

    [Fact]
    public async Task Serve_Warmup_ReportsReadyAndServes()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, stderr) = await Task.Run(() => CliTestHost.RunWithStdin(
            [TinyRequestLine()],
            "serve", "--model", CliTestHost.Fixture("model.onnx"),
            "--provider", "cpu",
            "--warmup-inputs", CliTestHost.Fixture("inputs.json")));

        Assert.Equal(0, exitCode);
        Assert.Contains("Warmup:", stderr);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], OutputData(lines[0]));
    }

    [Fact]
    public async Task Serve_BadRequestLine_EmitsErrorAndContinues()
    {
        if (!HasTinyFixture())
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            ["this is not json", TinyRequestLine()],
            "serve", "--model", CliTestHost.Fixture("model.onnx"), "--provider", "cpu"));

        // Server survives the bad line: error envelope + good response, exit 0 on EOF.
        Assert.Equal(0, exitCode);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        using var error = JsonDocument.Parse(lines[0]);
        Assert.True(error.RootElement.TryGetProperty("error", out _));
        Assert.Equal([11f, 22f, 13f, 24f, 15f, 26f], OutputData(lines[1]));
    }

    [Fact]
    public async Task Serve_MissingModel_Returns2()
    {
        var (exitCode, _, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            [],
            "serve", "--model", CliTestHost.Fixture("does-not-exist.onnx")));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Serve_MissingWarmupInputs_Returns2()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx")))
        {
            return;
        }

        var (exitCode, _, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            [],
            "serve", "--model", CliTestHost.Fixture("model.onnx"),
            "--warmup-inputs", CliTestHost.Fixture("does-not-exist.json")));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task Serve_EmptyStdin_ExitsZero()
    {
        if (!File.Exists(CliTestHost.Fixture("model.onnx")))
        {
            return;
        }

        var (exitCode, stdout, _) = await Task.Run(() => CliTestHost.RunWithStdin(
            [],
            "serve", "--model", CliTestHost.Fixture("model.onnx"), "--provider", "cpu"));

        Assert.Equal(0, exitCode);
        Assert.True(string.IsNullOrWhiteSpace(stdout));
    }
}
