using System.Text.Json;
using Xunit;

namespace LinML.Cli.Tests;

public class InspectCommandTests
{
    private static bool HasTinyFixture() => File.Exists(CliTestHost.Fixture("model.onnx"));

    [Fact]
    public void InspectCommand_MissingModel_ExitsWithCode2()
    {
        var (exitCode, _, stderr) = CliTestHost.Run("inspect");
        Assert.Equal(2, exitCode);
        Assert.Contains("--model", stderr);
    }

    [Fact]
    public void InspectCommand_NonExistentModel_ExitsWithCode2()
    {
        var (exitCode, _, stderr) = CliTestHost.Run("inspect", "--model", "non_existent.onnx");
        Assert.Equal(2, exitCode);
        Assert.Contains("not found", stderr);
    }

    [Fact]
    public void InspectCommand_ValidTinyModel_OutputsHumanReadableDiagnostics()
    {
        if (!HasTinyFixture()) return;

        var (exitCode, stdout, _) = CliTestHost.Run("inspect", "--model", CliTestHost.Fixture("model.onnx"));

        Assert.Equal(0, exitCode);
        Assert.Contains("LinML Model Inspection", stdout);
        Assert.Contains("Model:", stdout);
        Assert.Contains("SHA-256:", stdout);
        Assert.Contains("IR Version:", stdout);
        Assert.Contains("Inputs (1):", stdout);
        Assert.Contains("x: float32 [3, 2] (static)", stdout);
        Assert.Contains("Outputs (1):", stdout);
        Assert.Contains("y: float32 [3, 2] (static)", stdout);
        Assert.Contains("Graph Structure:", stdout);
        Assert.Contains("Execution Provider Compatibility:", stdout);
    }

    [Fact]
    public void InspectCommand_ValidTinyModel_Json_ReturnsValidSchema()
    {
        if (!HasTinyFixture()) return;

        var (exitCode, stdout, _) = CliTestHost.Run("inspect", "--model", CliTestHost.Fixture("model.onnx"), "--json");

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("model", out var modelProp));
        Assert.False(string.IsNullOrWhiteSpace(modelProp.GetProperty("path").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(modelProp.GetProperty("sha256").GetString()));
        Assert.True(modelProp.GetProperty("sizeBytes").GetInt64() > 0);

        Assert.True(root.TryGetProperty("irVersion", out var irProp));
        Assert.True(irProp.GetInt64() > 0);

        Assert.True(root.TryGetProperty("inputs", out var inputsProp));
        Assert.Equal(1, inputsProp.GetArrayLength());
        var input0 = inputsProp[0];
        Assert.Equal("x", input0.GetProperty("name").GetString());
        Assert.Equal("float32", input0.GetProperty("dataType").GetString());
        Assert.False(input0.GetProperty("isDynamic").GetBoolean());

        Assert.True(root.TryGetProperty("outputs", out var outputsProp));
        Assert.Equal(1, outputsProp.GetArrayLength());
        var output0 = outputsProp[0];
        Assert.Equal("y", output0.GetProperty("name").GetString());
        Assert.Equal("float32", output0.GetProperty("dataType").GetString());

        Assert.True(root.TryGetProperty("graph", out var graphProp));
        Assert.True(graphProp.GetProperty("nodeCount").GetInt32() > 0);
        Assert.True(graphProp.GetProperty("operators").GetArrayLength() > 0);

        Assert.True(root.TryGetProperty("compatibilityWarnings", out var warningsProp));
        Assert.Equal(JsonValueKind.Array, warningsProp.ValueKind);
    }
}
