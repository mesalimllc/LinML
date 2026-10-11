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
    public async Task Providers_UnknownSubcommand_ReturnsNonZero()
    {
        var (exitCode, _, _) = await Task.Run(() => CliTestHost.Run("providers", "bogus"));
        Assert.NotEqual(0, exitCode);
    }
}
